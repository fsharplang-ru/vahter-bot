module VahterBanBot.Tests.EventSerializationTests

open System.Text.Json
open VahterBanBot.Types
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
        """{"Case":"UserBanned","userId":999,"bannedBy":{"Case":"BannedByAI","chatId":123,"messageId":77,"messageText":"buy crypto","modelName":"gpt-4o-mini","promptHash":"cafebabe"}}"""
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
                      messageText = Some "buy crypto" |}
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
        """{"Case":"UserBanned","userId":555,"bannedBy":{"Case":"BannedByVahter","vahterId":42,"chatId":123,"messageId":77,"messageText":"some text"}}"""
    let event = JsonSerializer.Deserialize<UserEvent>(json, eventJsonOpts)
    let user = [event] |> List.fold (fun s e -> User.Fold(s, e)) User.Zero
    Assert.True(user.IsBanned)
    match user.BannedByActor with
    | Some (Actor.User u) ->
        Assert.Equal(42L, u.userId)
        Assert.Equal(None, u.username)  // old events don't have vahterUsername
    | other -> Assert.Fail $"Expected Actor.User but got {other}"

[<Fact>]
let ``Old UserBanned with BannedByAutoBan folds into Actor.Bot`` () =
    let json =
        """{"Case":"UserBanned","userId":555,"bannedBy":{"Case":"BannedByAutoBan","chatId":123,"messageText":"spam"}}"""
    let event = JsonSerializer.Deserialize<UserEvent>(json, eventJsonOpts)
    let user = [event] |> List.fold (fun s e -> User.Fold(s, e)) User.Zero
    Assert.True(user.IsBanned)
    match user.BannedByActor with
    | Some Actor.Bot -> ()
    | other -> Assert.Fail $"Expected Actor.Bot but got {other}"

[<Fact>]
let ``New Actor.ML UserBanned folds correctly`` () =
    let original =
        UserBanned {| userId = 777L
                      bannedBy = None
                      actor = Some Actor.ML
                      chatId = Some 123L
                      messageId = Some 77
                      messageText = Some "suspicious" |}
    let json = JsonSerializer.Serialize(original, eventJsonOpts)
    let event = JsonSerializer.Deserialize<UserEvent>(json, eventJsonOpts)
    let user = [event] |> List.fold (fun s e -> User.Fold(s, e)) User.Zero
    Assert.True(user.IsBanned)
    match user.BannedByActor with
    | Some Actor.ML -> ()
    | other -> Assert.Fail $"Expected Actor.ML but got {other}"

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

