module VahterBanBot.Tests.EventSerializationTests

open System
open System.Text.Json
open VahterBanBot.Types
open VahterBanBot.Utils
open Xunit

/// Tests that optional fields missing from JSONB deserialize to None (not an error).
/// This validates backward compatibility: events recorded before modelName/promptHash
/// were added must still deserialize correctly.

[<Fact>]
let ``LlmClassified without modelName and promptHash deserializes both as None`` () =
    let json =
        """{"Case":"LlmClassified","chatId":100,"messageId":42,"verdict":"KILL","promptTokens":200,"completionTokens":5,"latencyMs":350}"""
    let event = JsonSerializer.Deserialize<DetectionEvent>(json, eventJsonOpts)
    match event with
    | LlmClassified e ->
        Assert.Equal(None, e.modelName)
        Assert.Equal(None, e.promptHash)
        Assert.Equal("KILL", e.verdict)
    | other -> Assert.Fail $"Expected LlmClassified but got {other}"

[<Fact>]
let ``LlmClassified with modelName and promptHash deserializes correctly`` () =
    let json =
        """{"Case":"LlmClassified","chatId":100,"messageId":42,"verdict":"NOT_SPAM","promptTokens":150,"completionTokens":3,"latencyMs":400,"modelName":"gpt-4o-mini","promptHash":"abc123def456"}"""
    let event = JsonSerializer.Deserialize<DetectionEvent>(json, eventJsonOpts)
    match event with
    | LlmClassified e ->
        Assert.Equal(Some "gpt-4o-mini", e.modelName)
        Assert.Equal(Some "abc123def456", e.promptHash)
        Assert.Equal("NOT_SPAM", e.verdict)
    | other -> Assert.Fail $"Expected LlmClassified but got {other}"

[<Fact>]
let ``LlmClassified serialization round-trip preserves optional fields`` () =
    let original =
        LlmClassified {| chatId = 100L; messageId = 42; verdict = "SPAM"
                         promptTokens = 100; completionTokens = 5; latencyMs = 300
                         modelName = Some "gpt-4o-mini"; promptHash = Some "deadbeef" |}
    let json = JsonSerializer.Serialize(original, eventJsonOpts)
    let roundtripped = JsonSerializer.Deserialize<DetectionEvent>(json, eventJsonOpts)
    match roundtripped with
    | LlmClassified e ->
        Assert.Equal(Some "gpt-4o-mini", e.modelName)
        Assert.Equal(Some "deadbeef", e.promptHash)
        Assert.Equal("SPAM", e.verdict)
    | other -> Assert.Fail $"Expected LlmClassified but got {other}"

[<Fact>]
let ``Old BannedByAI event deserializes with backward compat`` () =
    // Simulates an old-format event stored in the database before the Actor migration
    let json =
        """{"Case":"UserBanned","userId":999,"bannedBy":{"Case":"BannedByAI","chatId":123,"messageId":77,"messageText":"buy crypto","modelName":"gpt-4o-mini","promptHash":"cafebabe"},"bannedAt":"2026-01-25T22:52:48Z"}"""
    let event = JsonSerializer.Deserialize<UserEvent>(json, eventJsonOpts)
    match event with
    | UserBanned e ->
        Assert.Equal(999L, e.userId)
        Assert.Equal(None, e.actor)
        match e.bannedBy with
        | Some (BannedByAI a) ->
            Assert.Equal(123L, a.chatId)
            Assert.Equal(77, a.messageId)
            Assert.Equal(Some "buy crypto", a.messageText)
            Assert.Equal("gpt-4o-mini", a.modelName)
            Assert.Equal("cafebabe", a.promptHash)
        | other -> Assert.Fail $"Expected Some BannedByAI but got {other}"
    | other -> Assert.Fail $"Expected UserBanned but got {other}"

[<Fact>]
let ``New Actor-format UserBanned round-trips correctly`` () =
    let original =
        UserBanned {| userId = 888L
                      bannedBy = None
                      actor = Some (Actor.LLM {| modelName = "gpt-4o-mini"; promptHash = "cafebabe" |})
                      chatId = Some 123L
                      messageId = Some 77
                      messageText = Some "buy crypto"
                      bannedAt = DateTime(2026, 1, 25, 22, 52, 48, DateTimeKind.Utc) |}
    let json = JsonSerializer.Serialize(original, eventJsonOpts)
    let roundtripped = JsonSerializer.Deserialize<UserEvent>(json, eventJsonOpts)
    match roundtripped with
    | UserBanned e ->
        Assert.Equal(888L, e.userId)
        Assert.Equal(None, e.bannedBy)
        match e.actor with
        | Some (Actor.LLM l) ->
            Assert.Equal("gpt-4o-mini", l.modelName)
            Assert.Equal("cafebabe", l.promptHash)
        | other -> Assert.Fail $"Expected Some Actor.LLM but got {other}"
        Assert.Equal(Some 123L, e.chatId)
        Assert.Equal(Some 77, e.messageId)
        Assert.Equal(Some "buy crypto", e.messageText)
    | other -> Assert.Fail $"Expected UserBanned but got {other}"

[<Fact>]
let ``Old UserBanned with BannedByVahter folds into Actor.User`` () =
    // Actual old-format event from production (before vahterUsername was added)
    let json =
        """{"Case":"UserBanned","userId":555,"bannedBy":{"Case":"BannedByVahter","vahterId":42,"chatId":123,"messageId":77,"messageText":"some text"},"bannedAt":"2026-01-25T22:52:48Z"}"""
    let event = JsonSerializer.Deserialize<UserEvent>(json, eventJsonOpts)
    let user = [event] |> List.fold (fun s e -> User.Fold(s, e)) User.Zero
    Assert.True(user.Banned.IsSome)
    match user.Banned with
    | Some (Actor.User u, _) ->
        Assert.Equal(42L, u.userId)
        Assert.Equal(None, u.username)  // old events don't have vahterUsername
    | other -> Assert.Fail $"Expected Actor.User but got {other}"

[<Fact>]
let ``Old UserBanned with BannedByAutoBan folds into Actor.Bot`` () =
    let json =
        """{"Case":"UserBanned","userId":555,"bannedBy":{"Case":"BannedByAutoBan","chatId":123,"messageText":"spam"},"bannedAt":"2026-01-25T22:52:48Z"}"""
    let event = JsonSerializer.Deserialize<UserEvent>(json, eventJsonOpts)
    let user = [event] |> List.fold (fun s e -> User.Fold(s, e)) User.Zero
    Assert.True(user.Banned.IsSome)
    match user.Banned with
    | Some (Actor.Bot None, _) -> ()
    | other -> Assert.Fail $"Expected Actor.Bot but got {other}"

[<Fact>]
let ``New Actor.ML UserBanned folds correctly`` () =
    let original =
        UserBanned {| userId = 777L
                      bannedBy = None
                      actor = Some Actor.ML
                      chatId = Some 123L
                      messageId = Some 77
                      messageText = Some "suspicious"
                      bannedAt = DateTime.UtcNow |}
    let json = JsonSerializer.Serialize(original, eventJsonOpts)
    let event = JsonSerializer.Deserialize<UserEvent>(json, eventJsonOpts)
    let user = [event] |> List.fold (fun s e -> User.Fold(s, e)) User.Zero
    Assert.True(user.Banned.IsSome)
    match user.Banned with
    | Some (Actor.ML, _) -> ()
    | other -> Assert.Fail $"Expected Actor.ML but got {other}"

[<Fact>]
let ``Old UserReactionRecorded without chatId/messageId/emoji deserializes with None`` () =
    // Pre-PR events lack chatId/messageId/emoji. They must still parse so old streams remain readable.
    let json =
        """{"Case":"UserReactionRecorded","userId":42,"delta":1}"""
    let event = JsonSerializer.Deserialize<UserEvent>(json, eventJsonOpts)
    match event with
    | UserReactionRecorded e ->
        Assert.Equal(42L, e.userId)
        Assert.Equal(1, e.delta)
        Assert.Equal(None, e.chatId)
        Assert.Equal(None, e.messageId)
        Assert.Equal(None, e.emoji)
    | other -> Assert.Fail $"Expected UserReactionRecorded but got {other}"

[<Fact>]
let ``New UserReactionRecorded with chatId/messageId/emoji round-trips`` () =
    let original =
        UserReactionRecorded {| userId = 42L; chatId = Some 123L; messageId = Some 7; emoji = Some "🔥"; delta = 1 |}
    let json = JsonSerializer.Serialize(original, eventJsonOpts)
    let roundtripped = JsonSerializer.Deserialize<UserEvent>(json, eventJsonOpts)
    match roundtripped with
    | UserReactionRecorded e ->
        Assert.Equal(Some 123L, e.chatId)
        Assert.Equal(Some 7, e.messageId)
        Assert.Equal(Some "🔥", e.emoji)
        Assert.Equal(1, e.delta)
    | other -> Assert.Fail $"Expected UserReactionRecorded but got {other}"

[<Fact>]
let ``ReactionTriageNotSpamSet round-trips and folds into User.NotSpamUntil`` () =
    let until = DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc)
    let original =
        ReactionTriageNotSpamSet {| userId = 100L; until = until; actor = Actor.User {| userId = 34L; username = Some "vahter_1" |} |}
    let json = JsonSerializer.Serialize(original, eventJsonOpts)
    let event = JsonSerializer.Deserialize<UserEvent>(json, eventJsonOpts)
    let user = [event] |> List.fold (fun s e -> User.Fold(s, e)) User.Zero
    Assert.Equal(Some until, user.NotSpamUntil)

[<Fact>]
let ``LlmReactionTriageClassified round-trips with reason and shadowMode`` () =
    let original =
        LlmReactionTriageClassified
            {| chatId = -666L; userId = 42L; verdict = "UNSURE"; reason = Some "bio empty, photo unclear"
               promptTokens = 200; completionTokens = 12; latencyMs = 450
               modelName = Some "gpt-4o-mini"; promptHash = Some "abc"; shadowMode = true |}
    let json = JsonSerializer.Serialize(original, eventJsonOpts)
    let roundtripped = JsonSerializer.Deserialize<DetectionEvent>(json, eventJsonOpts)
    match roundtripped with
    | LlmReactionTriageClassified e ->
        Assert.Equal("UNSURE", e.verdict)
        Assert.Equal(Some "bio empty, photo unclear", e.reason)
        Assert.True(e.shadowMode)
    | other -> Assert.Fail $"Expected LlmReactionTriageClassified but got {other}"

[<Fact>]
let ``Old UserUnbanned without actor deserializes correctly`` () =
    let json =
        """{"Case":"UserUnbanned","userId":555,"unbannedBy":42}"""
    let event = JsonSerializer.Deserialize<UserEvent>(json, eventJsonOpts)
    match event with
    | UserUnbanned e ->
        Assert.Equal(555L, e.userId)
        Assert.Equal(Some 42L, e.unbannedBy)
        Assert.Equal(None, e.actor)
    | other -> Assert.Fail $"Expected UserUnbanned but got {other}"

// ---------------------------------------------------------------------------
// Snapshot DTO serialization. These guard the contract between the F# DTO keys
// and the GENERATED-column expressions in V38__snapshot.sql — a key rename here
// silently turns a generated column into all-NULLs, so assert the exact keys.
// ---------------------------------------------------------------------------

let private snapJson (o: obj) = JsonSerializer.Serialize(o, snapshotJsonOpts)
let private prop (json: string) (name: string) =
    use doc = JsonDocument.Parse json
    match doc.RootElement.TryGetProperty name with
    | true, v -> Some (v.Clone())
    | _ -> None

[<Fact>]
let ``userSnapshot of a banned user emits the generated-column keys`` () =
    let bannedAt = DateTime(2026, 6, 27, 10, 0, 0, DateTimeKind.Utc)
    let user =
        { User.Zero with
            Id = 555L
            Username = Some "alice"
            Banned = Some (Actor.User {| userId = 42L; username = Some "vahter_1" |}, bannedAt)
            ReactionCount = 7 }
    let json = snapJson (userSnapshot user)
    Assert.Equal(Some "alice", prop json "username" |> Option.map (fun v -> v.GetString()))
    Assert.Equal(Some true,    prop json "banned"   |> Option.map (fun v -> v.GetBoolean()))
    Assert.Equal(Some 42L,     prop json "bannedByUserId" |> Option.map (fun v -> v.GetInt64()))
    Assert.Equal(Some 7,       prop json "reactionCount"  |> Option.map (fun v -> v.GetInt32()))
    // bannedAt must be a UTC instant with a Z offset so the IMMUTABLE timestamptz cast is deterministic.
    let bannedAtStr = prop json "bannedAt" |> Option.map (fun v -> v.GetString())
    Assert.True(bannedAtStr |> Option.exists (fun s -> s.EndsWith "Z"), $"bannedAt should be UTC/Z, got {bannedAtStr}")

[<Fact>]
let ``userSnapshot of a non-banned user omits ban keys (NULL columns)`` () =
    let user = { User.Zero with Id = 1L; Username = Some "bob" }
    let json = snapJson (userSnapshot user)
    Assert.Equal(Some false, prop json "banned" |> Option.map (fun v -> v.GetBoolean()))
    Assert.Equal(None, prop json "bannedAt")        // skipped option -> absent -> NULL column
    Assert.Equal(None, prop json "bannedByUserId")

[<Fact>]
let ``messageSnapshot maps classification and carries text/userId`` () =
    let msg = { Message.Zero with Received = true; Text = Some "hello"; UserId = Some 99L; Classification = SpamClassification.Ham }
    let json = snapJson (messageSnapshot msg)
    Assert.Equal(Some "hello", prop json "text" |> Option.map (fun v -> v.GetString()))
    Assert.Equal(Some 99L,     prop json "userId" |> Option.map (fun v -> v.GetInt64()))
    Assert.Equal(Some "Ham",   prop json "classification" |> Option.map (fun v -> v.GetString()))
    Assert.Equal(Some false,   prop json "deleted" |> Option.map (fun v -> v.GetBoolean()))

[<Theory>]
[<InlineData("PotentialKill", "Spam")>]
[<InlineData("ManualBan", "Spam")>]
[<InlineData("ReactionTriageBan", "Spam")>]
[<InlineData("PotentialNotSpam", "NotSpam")>]
[<InlineData("DetectedNotSpam", "NotSpam")>]
let ``moderationSnapshot maps the last vahter action to a verdict`` (action: string, expected: string) =
    let act =
        match action with
        | "PotentialKill" -> PotentialKill
        | "ManualBan" -> ManualBan
        | "ReactionTriageBan" -> ReactionTriageBan
        | "PotentialNotSpam" -> PotentialNotSpam
        | "DetectedNotSpam" -> DetectedNotSpam
        | other -> failwith $"unmapped {other}"
    let m = { Moderation.Zero with VahterActedCount = 1; LastVahterAction = Some act }
    let json = snapJson (moderationSnapshot m)
    Assert.Equal(Some expected, prop json "verdict" |> Option.map (fun v -> v.GetString()))

[<Fact>]
let ``moderationSnapshot with only a bot auto-delete has no verdict but flags botAutoDeleted`` () =
    let m = { Moderation.Zero with BotAutoDeletedCount = 1 }
    let json = snapJson (moderationSnapshot m)
    Assert.Equal(Some true, prop json "botAutoDeleted" |> Option.map (fun v -> v.GetBoolean()))
    Assert.Equal(None, prop json "verdict")   // no vahter action -> absent -> NULL column

// rawMessage exists in production as BOTH a JSON string (live app) and a JSON object (legacy
// backfill) — issue #166. Folding must tolerate both, otherwise rebuild / mark-ham on old
// messages throws "Cannot get the value of a token type 'StartObject' as a string".

[<Fact>]
let ``MessageReceived with rawMessage as a JSON string deserializes`` () =
    let json =
        """{"Case":"MessageReceived","chatId":-666,"messageId":1,"userId":42,"text":"hi","rawMessage":"{\"text\":\"hi\"}"}"""
    match JsonSerializer.Deserialize<MessageEvent>(json, eventJsonOpts) with
    | MessageReceived e ->
        Assert.Equal(-666L, e.chatId)
        Assert.Equal(Some "hi", e.text)
    | other -> Assert.Fail $"Expected MessageReceived but got {other}"

[<Fact>]
let ``MessageReceived with rawMessage as a JSON object deserializes (legacy backfill shape)`` () =
    let json =
        """{"Case":"MessageReceived","chatId":-666,"messageId":2,"userId":42,"text":"hi","rawMessage":{"text":"hi","entities":[]}}"""
    match JsonSerializer.Deserialize<MessageEvent>(json, eventJsonOpts) with
    | MessageReceived e ->
        Assert.Equal(-666L, e.chatId)
        Assert.Equal(Some "hi", e.text)
    | other -> Assert.Fail $"Expected MessageReceived but got {other}"


// ---------------------------------------------------------------------------
// Message.FoldTimeline — a message's spam/ham status folded in F# from BOTH streams
// (message marks + moderation bot/vahter actions), last decisive event wins. Mirrors MlData.
// Pure: the caller feeds events already ordered by (created_at, id).
// ---------------------------------------------------------------------------

let private rawEl () = JsonSerializer.SerializeToElement("{}")
let private recv () = FromMessage (MessageReceived {| chatId = -1L; messageId = 1; userId = 5L; text = Some "x"; rawMessage = rawEl () |})
let private botDeleted () = FromModeration (BotAutoDeleted {| chatId = -1L; messageId = 1; userId = 5L; reason = MlSpam {| score = 4.0 |} |})
let private vahter (act: VahterAction) = FromModeration (VahterActed {| vahterId = 34L; actionType = act; targetUserId = 5L; chatId = -1L; messageId = 1 |})
let private markSpam () = FromMessage (MessageMarkedSpam {| chatId = -1L; messageId = 1; markedBy = Some 34L |})
let private markHam () = FromMessage (MessageMarkedHam {| chatId = -1L; messageId = 1; text = "x"; markedBy = Some 34L |})
let private foldTl evs = evs |> List.fold (fun s e -> Message.FoldTimeline(s, e)) Message.Zero

[<Fact>]
let ``FoldTimeline: plain received message has Unknown status`` () =
    Assert.Equal(SpamClassification.Unknown, (foldTl [ recv () ]).Classification)

[<Fact>]
let ``FoldTimeline: bot auto-delete -> Spam`` () =
    Assert.Equal(SpamClassification.Spam, (foldTl [ recv (); botDeleted () ]).Classification)

[<Fact>]
let ``FoldTimeline: vahter ManualBan -> Spam`` () =
    Assert.Equal(SpamClassification.Spam, (foldTl [ recv (); vahter ManualBan ]).Classification)

[<Fact>]
let ``FoldTimeline: vahter DetectedNotSpam -> Ham`` () =
    Assert.Equal(SpamClassification.Ham, (foldTl [ recv (); vahter DetectedNotSpam ]).Classification)

[<Fact>]
let ``FoldTimeline: reaction-triage / soft-spam are non-decisive`` () =
    Assert.Equal(SpamClassification.Unknown, (foldTl [ recv (); vahter ReactionTriageBan; vahter PotentialSoftSpam ]).Classification)

[<Fact>]
let ``FoldTimeline: marked spam then moderation not-spam (later) -> Ham`` () =
    // cross-stream last-wins: a later vahter not-spam overrides an earlier spam mark
    Assert.Equal(SpamClassification.Ham, (foldTl [ recv (); markSpam (); vahter DetectedNotSpam ]).Classification)

[<Fact>]
let ``FoldTimeline: bot-deleted then marked ham (later) -> Ham`` () =
    Assert.Equal(SpamClassification.Ham, (foldTl [ recv (); botDeleted (); markHam () ]).Classification)

[<Fact>]
let ``FoldTimeline: marked ham then bot-deleted (later) -> Spam`` () =
    Assert.Equal(SpamClassification.Spam, (foldTl [ recv (); markHam (); botDeleted () ]).Classification)

[<Fact>]
let ``FoldTimeline: keeps message fields (text/received) regardless of moderation`` () =
    let m = foldTl [ recv (); botDeleted () ]
    Assert.True(m.Received)
    Assert.Equal(Some "x", m.Text)
    Assert.Equal(Some 5L, m.UserId)
