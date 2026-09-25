module VahterBanBot.Unit.Tests.UserSnapshotTests

open System
open System.Text.Json
open Microsoft.FSharp.Reflection
open BotInfra
open VahterBanBot.Types
open Xunit

let private t0 = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private userId = 42L

/// Every UserEvent case, every Actor case, the legacy `bannedBy`-only ban shape, and edge cases
/// (consume/revoke without a grant, unban while not banned, negative reaction count).
let private canonicalEvents : UserEvent list =
    let ban (actor: Actor option) (bannedBy: BannedBy option) (at: DateTime) =
        UserBanned {| userId = userId; bannedBy = bannedBy; actor = actor; chatId = Some -100L
                      messageId = Some 7L; messageText = Some "spam"; bannedAt = at |}
    [ SpamProtectionConsumed {| userId = userId; chatId = -100L; messageId = 1L |}
      SpamProtectionRevoked {| userId = userId; reason = "killed" |}
      UserUnbanned {| userId = userId; unbannedBy = Some 9L; actor = None |}
      UserReactionRecorded {| userId = userId; chatId = None; messageId = None; emoji = None; delta = -3 |}
      UsernameChanged {| userId = userId; username = Some "alice" |}
      UserReactionRecorded {| userId = userId; chatId = Some -100L; messageId = Some 5L; emoji = Some "🔥"; delta = 2 |}
      UserReactionRecorded {| userId = userId; chatId = None; messageId = None; emoji = None; delta = -1 |}
      ban None (Some (BannedByVahter {| vahterId = 9L; vahterUsername = Some "v"; chatId = -100L; messageId = 7; messageText = None |})) t0
      UserUnbanned {| userId = userId; unbannedBy = Some 9L; actor = None |}
      ban None (Some (BannedByAI {| chatId = -100L; messageId = 8; messageText = None; modelName = "m"; promptHash = "h" |}))
          (t0.AddDays 1.0)
      UserUnbanned {| userId = userId; unbannedBy = None; actor = Some Actor.ML |}
      ReactionTriageNotSpamSet {| userId = userId; until = t0.AddDays 2.0; actor = Actor.LLM {| modelName = "m"; promptHash = "h" |} |}
      SpamProtectionGranted {| userId = userId; until = t0.AddDays 3.0; chatId = -100L; messageId = 11L; vahterId = 9L |}
      SpamProtectionConsumed {| userId = userId; chatId = -100L; messageId = 12L |}
      SpamProtectionRevoked {| userId = userId; reason = "budget" |}
      SpamProtectionGranted {| userId = userId; until = t0.AddDays 4.0; chatId = -100L; messageId = 13L; vahterId = 9L |}
      SpamProtectionConsumed {| userId = userId; chatId = -100L; messageId = 14L |}
      ban (Some (Actor.Bot (Some {| botUserId = 1L; botUsername = "vahter" |}))) None (t0.AddDays 5.0)
      UsernameChanged {| userId = userId; username = None |}
      ban (Some (Actor.User {| userId = 9L; username = Some "v" |})) None (t0.AddDays 6.0) ]

let private foldAll (seed: User) (events: UserEvent list) = events |> List.fold (fun s e -> User.Fold(s, e)) seed

let private roundTrip (u: User) =
    JsonSerializer.Deserialize<User>(JsonSerializer.Serialize(u, eventJsonOpts), eventJsonOpts)

// Append a new (SchemaVersion, shape) pair whenever User changes; never edit an existing pair.
let private pinnedShapes =
    [ 1, "User{Id: System.Int64; Banned: option<(Actor[User of (Item: {userId: System.Int64; username: option<System.String>}) | Bot of (Item: option<{botUserId: System.Int64; botUsername: System.String}>) | ML | LLM of (Item: {modelName: System.String; promptHash: System.String})] * System.DateTime)>; Username: option<System.String>; ReactionCount: System.Int32; NotSpamUntil: option<System.DateTime>; SpamProtectionUntil: option<System.DateTime>; SpamProtectionHits: System.Int32}" ]

/// Fully-populated state, so a Fold case that touches a field it shouldn't shows up in the transcript.
let private richSeed =
    { Id = 7L
      Banned = Some (Actor.LLM {| modelName = "seed"; promptHash = "seed" |}, t0.AddDays -1.0)
      Username = Some "seed"
      ReactionCount = 7
      NotSpamUntil = Some (t0.AddDays 9.0)
      SpamProtectionUntil = Some (t0.AddDays 9.0)
      SpamProtectionHits = 2 }

/// Every intermediate state of the canonical sequence plus each event applied to Zero and to a
/// fully-populated state, as snapshot JSON — pins Fold's behaviour AND the stored format.
let private foldTranscript () =
    let json (u: User) = JsonSerializer.Serialize(u, eventJsonOpts)
    let case (e: UserEvent) = (FSharpValue.GetUnionFields(e, typeof<UserEvent>) |> fst).Name
    let fold s e = User.Fold(s, e)
    [ for e, s in List.zip canonicalEvents (List.tail (List.scan fold User.Zero canonicalEvents)) do
          yield $"seq  {case e} -> {json s}"
      for e in canonicalEvents do
          yield $"zero {case e} -> {json (fold User.Zero e)}"
          yield $"rich {case e} -> {json (fold richSeed e)}" ]
    |> String.concat "\n"

// Append a new (SchemaVersion, transcript) pair whenever Fold or the snapshot JSON changes.
let private pinnedFolds =
    [ 1,
      String.concat "\n" [
          """seq  SpamProtectionConsumed -> {"Id":42,"ReactionCount":0,"SpamProtectionHits":1}"""
          """seq  SpamProtectionRevoked -> {"Id":42,"ReactionCount":0,"SpamProtectionHits":0}"""
          """seq  UserUnbanned -> {"Id":42,"ReactionCount":0,"SpamProtectionHits":0}"""
          """seq  UserReactionRecorded -> {"Id":42,"ReactionCount":-3,"SpamProtectionHits":0}"""
          """seq  UsernameChanged -> {"Id":42,"Username":"alice","ReactionCount":-3,"SpamProtectionHits":0}"""
          """seq  UserReactionRecorded -> {"Id":42,"Username":"alice","ReactionCount":-1,"SpamProtectionHits":0}"""
          """seq  UserReactionRecorded -> {"Id":42,"Username":"alice","ReactionCount":-2,"SpamProtectionHits":0}"""
          """seq  UserBanned -> {"Id":42,"Banned":[{"Case":"User","userId":9,"username":"v"},"2026-01-01T00:00:00Z"],"Username":"alice","ReactionCount":-2,"SpamProtectionHits":0}"""
          """seq  UserUnbanned -> {"Id":42,"Username":"alice","ReactionCount":-2,"SpamProtectionHits":0}"""
          """seq  UserBanned -> {"Id":42,"Banned":[{"Case":"LLM","modelName":"m","promptHash":"h"},"2026-01-02T00:00:00Z"],"Username":"alice","ReactionCount":-2,"SpamProtectionHits":0}"""
          """seq  UserUnbanned -> {"Id":42,"Username":"alice","ReactionCount":-2,"SpamProtectionHits":0}"""
          """seq  ReactionTriageNotSpamSet -> {"Id":42,"Username":"alice","ReactionCount":-2,"NotSpamUntil":"2026-01-03T00:00:00Z","SpamProtectionHits":0}"""
          """seq  SpamProtectionGranted -> {"Id":42,"Username":"alice","ReactionCount":-2,"NotSpamUntil":"2026-01-03T00:00:00Z","SpamProtectionUntil":"2026-01-04T00:00:00Z","SpamProtectionHits":0}"""
          """seq  SpamProtectionConsumed -> {"Id":42,"Username":"alice","ReactionCount":-2,"NotSpamUntil":"2026-01-03T00:00:00Z","SpamProtectionUntil":"2026-01-04T00:00:00Z","SpamProtectionHits":1}"""
          """seq  SpamProtectionRevoked -> {"Id":42,"Username":"alice","ReactionCount":-2,"NotSpamUntil":"2026-01-03T00:00:00Z","SpamProtectionHits":0}"""
          """seq  SpamProtectionGranted -> {"Id":42,"Username":"alice","ReactionCount":-2,"NotSpamUntil":"2026-01-03T00:00:00Z","SpamProtectionUntil":"2026-01-05T00:00:00Z","SpamProtectionHits":0}"""
          """seq  SpamProtectionConsumed -> {"Id":42,"Username":"alice","ReactionCount":-2,"NotSpamUntil":"2026-01-03T00:00:00Z","SpamProtectionUntil":"2026-01-05T00:00:00Z","SpamProtectionHits":1}"""
          """seq  UserBanned -> {"Id":42,"Banned":[{"Case":"Bot","Item":{"botUserId":1,"botUsername":"vahter"}},"2026-01-06T00:00:00Z"],"Username":"alice","ReactionCount":-2,"NotSpamUntil":"2026-01-03T00:00:00Z","SpamProtectionUntil":"2026-01-05T00:00:00Z","SpamProtectionHits":1}"""
          """seq  UsernameChanged -> {"Id":42,"Banned":[{"Case":"Bot","Item":{"botUserId":1,"botUsername":"vahter"}},"2026-01-06T00:00:00Z"],"ReactionCount":-2,"NotSpamUntil":"2026-01-03T00:00:00Z","SpamProtectionUntil":"2026-01-05T00:00:00Z","SpamProtectionHits":1}"""
          """seq  UserBanned -> {"Id":42,"Banned":[{"Case":"User","userId":9,"username":"v"},"2026-01-07T00:00:00Z"],"ReactionCount":-2,"NotSpamUntil":"2026-01-03T00:00:00Z","SpamProtectionUntil":"2026-01-05T00:00:00Z","SpamProtectionHits":1}"""
          """zero SpamProtectionConsumed -> {"Id":42,"ReactionCount":0,"SpamProtectionHits":1}"""
          """rich SpamProtectionConsumed -> {"Id":42,"Banned":[{"Case":"LLM","modelName":"seed","promptHash":"seed"},"2025-12-31T00:00:00Z"],"Username":"seed","ReactionCount":7,"NotSpamUntil":"2026-01-10T00:00:00Z","SpamProtectionUntil":"2026-01-10T00:00:00Z","SpamProtectionHits":3}"""
          """zero SpamProtectionRevoked -> {"Id":42,"ReactionCount":0,"SpamProtectionHits":0}"""
          """rich SpamProtectionRevoked -> {"Id":42,"Banned":[{"Case":"LLM","modelName":"seed","promptHash":"seed"},"2025-12-31T00:00:00Z"],"Username":"seed","ReactionCount":7,"NotSpamUntil":"2026-01-10T00:00:00Z","SpamProtectionHits":0}"""
          """zero UserUnbanned -> {"Id":42,"ReactionCount":0,"SpamProtectionHits":0}"""
          """rich UserUnbanned -> {"Id":42,"Username":"seed","ReactionCount":7,"NotSpamUntil":"2026-01-10T00:00:00Z","SpamProtectionUntil":"2026-01-10T00:00:00Z","SpamProtectionHits":2}"""
          """zero UserReactionRecorded -> {"Id":42,"ReactionCount":-3,"SpamProtectionHits":0}"""
          """rich UserReactionRecorded -> {"Id":42,"Banned":[{"Case":"LLM","modelName":"seed","promptHash":"seed"},"2025-12-31T00:00:00Z"],"Username":"seed","ReactionCount":4,"NotSpamUntil":"2026-01-10T00:00:00Z","SpamProtectionUntil":"2026-01-10T00:00:00Z","SpamProtectionHits":2}"""
          """zero UsernameChanged -> {"Id":42,"Username":"alice","ReactionCount":0,"SpamProtectionHits":0}"""
          """rich UsernameChanged -> {"Id":42,"Banned":[{"Case":"LLM","modelName":"seed","promptHash":"seed"},"2025-12-31T00:00:00Z"],"Username":"alice","ReactionCount":7,"NotSpamUntil":"2026-01-10T00:00:00Z","SpamProtectionUntil":"2026-01-10T00:00:00Z","SpamProtectionHits":2}"""
          """zero UserReactionRecorded -> {"Id":42,"ReactionCount":2,"SpamProtectionHits":0}"""
          """rich UserReactionRecorded -> {"Id":42,"Banned":[{"Case":"LLM","modelName":"seed","promptHash":"seed"},"2025-12-31T00:00:00Z"],"Username":"seed","ReactionCount":9,"NotSpamUntil":"2026-01-10T00:00:00Z","SpamProtectionUntil":"2026-01-10T00:00:00Z","SpamProtectionHits":2}"""
          """zero UserReactionRecorded -> {"Id":42,"ReactionCount":-1,"SpamProtectionHits":0}"""
          """rich UserReactionRecorded -> {"Id":42,"Banned":[{"Case":"LLM","modelName":"seed","promptHash":"seed"},"2025-12-31T00:00:00Z"],"Username":"seed","ReactionCount":6,"NotSpamUntil":"2026-01-10T00:00:00Z","SpamProtectionUntil":"2026-01-10T00:00:00Z","SpamProtectionHits":2}"""
          """zero UserBanned -> {"Id":42,"Banned":[{"Case":"User","userId":9,"username":"v"},"2026-01-01T00:00:00Z"],"ReactionCount":0,"SpamProtectionHits":0}"""
          """rich UserBanned -> {"Id":42,"Banned":[{"Case":"User","userId":9,"username":"v"},"2026-01-01T00:00:00Z"],"Username":"seed","ReactionCount":7,"NotSpamUntil":"2026-01-10T00:00:00Z","SpamProtectionUntil":"2026-01-10T00:00:00Z","SpamProtectionHits":2}"""
          """zero UserUnbanned -> {"Id":42,"ReactionCount":0,"SpamProtectionHits":0}"""
          """rich UserUnbanned -> {"Id":42,"Username":"seed","ReactionCount":7,"NotSpamUntil":"2026-01-10T00:00:00Z","SpamProtectionUntil":"2026-01-10T00:00:00Z","SpamProtectionHits":2}"""
          """zero UserBanned -> {"Id":42,"Banned":[{"Case":"LLM","modelName":"m","promptHash":"h"},"2026-01-02T00:00:00Z"],"ReactionCount":0,"SpamProtectionHits":0}"""
          """rich UserBanned -> {"Id":42,"Banned":[{"Case":"LLM","modelName":"m","promptHash":"h"},"2026-01-02T00:00:00Z"],"Username":"seed","ReactionCount":7,"NotSpamUntil":"2026-01-10T00:00:00Z","SpamProtectionUntil":"2026-01-10T00:00:00Z","SpamProtectionHits":2}"""
          """zero UserUnbanned -> {"Id":42,"ReactionCount":0,"SpamProtectionHits":0}"""
          """rich UserUnbanned -> {"Id":42,"Username":"seed","ReactionCount":7,"NotSpamUntil":"2026-01-10T00:00:00Z","SpamProtectionUntil":"2026-01-10T00:00:00Z","SpamProtectionHits":2}"""
          """zero ReactionTriageNotSpamSet -> {"Id":42,"ReactionCount":0,"NotSpamUntil":"2026-01-03T00:00:00Z","SpamProtectionHits":0}"""
          """rich ReactionTriageNotSpamSet -> {"Id":42,"Banned":[{"Case":"LLM","modelName":"seed","promptHash":"seed"},"2025-12-31T00:00:00Z"],"Username":"seed","ReactionCount":7,"NotSpamUntil":"2026-01-03T00:00:00Z","SpamProtectionUntil":"2026-01-10T00:00:00Z","SpamProtectionHits":2}"""
          """zero SpamProtectionGranted -> {"Id":42,"ReactionCount":0,"SpamProtectionUntil":"2026-01-04T00:00:00Z","SpamProtectionHits":0}"""
          """rich SpamProtectionGranted -> {"Id":42,"Banned":[{"Case":"LLM","modelName":"seed","promptHash":"seed"},"2025-12-31T00:00:00Z"],"Username":"seed","ReactionCount":7,"NotSpamUntil":"2026-01-10T00:00:00Z","SpamProtectionUntil":"2026-01-04T00:00:00Z","SpamProtectionHits":0}"""
          """zero SpamProtectionConsumed -> {"Id":42,"ReactionCount":0,"SpamProtectionHits":1}"""
          """rich SpamProtectionConsumed -> {"Id":42,"Banned":[{"Case":"LLM","modelName":"seed","promptHash":"seed"},"2025-12-31T00:00:00Z"],"Username":"seed","ReactionCount":7,"NotSpamUntil":"2026-01-10T00:00:00Z","SpamProtectionUntil":"2026-01-10T00:00:00Z","SpamProtectionHits":3}"""
          """zero SpamProtectionRevoked -> {"Id":42,"ReactionCount":0,"SpamProtectionHits":0}"""
          """rich SpamProtectionRevoked -> {"Id":42,"Banned":[{"Case":"LLM","modelName":"seed","promptHash":"seed"},"2025-12-31T00:00:00Z"],"Username":"seed","ReactionCount":7,"NotSpamUntil":"2026-01-10T00:00:00Z","SpamProtectionHits":0}"""
          """zero SpamProtectionGranted -> {"Id":42,"ReactionCount":0,"SpamProtectionUntil":"2026-01-05T00:00:00Z","SpamProtectionHits":0}"""
          """rich SpamProtectionGranted -> {"Id":42,"Banned":[{"Case":"LLM","modelName":"seed","promptHash":"seed"},"2025-12-31T00:00:00Z"],"Username":"seed","ReactionCount":7,"NotSpamUntil":"2026-01-10T00:00:00Z","SpamProtectionUntil":"2026-01-05T00:00:00Z","SpamProtectionHits":0}"""
          """zero SpamProtectionConsumed -> {"Id":42,"ReactionCount":0,"SpamProtectionHits":1}"""
          """rich SpamProtectionConsumed -> {"Id":42,"Banned":[{"Case":"LLM","modelName":"seed","promptHash":"seed"},"2025-12-31T00:00:00Z"],"Username":"seed","ReactionCount":7,"NotSpamUntil":"2026-01-10T00:00:00Z","SpamProtectionUntil":"2026-01-10T00:00:00Z","SpamProtectionHits":3}"""
          """zero UserBanned -> {"Id":42,"Banned":[{"Case":"Bot","Item":{"botUserId":1,"botUsername":"vahter"}},"2026-01-06T00:00:00Z"],"ReactionCount":0,"SpamProtectionHits":0}"""
          """rich UserBanned -> {"Id":42,"Banned":[{"Case":"Bot","Item":{"botUserId":1,"botUsername":"vahter"}},"2026-01-06T00:00:00Z"],"Username":"seed","ReactionCount":7,"NotSpamUntil":"2026-01-10T00:00:00Z","SpamProtectionUntil":"2026-01-10T00:00:00Z","SpamProtectionHits":2}"""
          """zero UsernameChanged -> {"Id":42,"ReactionCount":0,"SpamProtectionHits":0}"""
          """rich UsernameChanged -> {"Id":42,"Banned":[{"Case":"LLM","modelName":"seed","promptHash":"seed"},"2025-12-31T00:00:00Z"],"ReactionCount":7,"NotSpamUntil":"2026-01-10T00:00:00Z","SpamProtectionUntil":"2026-01-10T00:00:00Z","SpamProtectionHits":2}"""
          """zero UserBanned -> {"Id":42,"Banned":[{"Case":"User","userId":9,"username":"v"},"2026-01-07T00:00:00Z"],"ReactionCount":0,"SpamProtectionHits":0}"""
          """rich UserBanned -> {"Id":42,"Banned":[{"Case":"User","userId":9,"username":"v"},"2026-01-07T00:00:00Z"],"Username":"seed","ReactionCount":7,"NotSpamUntil":"2026-01-10T00:00:00Z","SpamProtectionUntil":"2026-01-10T00:00:00Z","SpamProtectionHits":2}""" ] ]

[<Fact>]
let ``User shape is pinned to the current snapshot SchemaVersion`` () =
    let version, shape = List.last pinnedShapes
    let actual = SnapshotShape.describe typeof<User>
    Assert.True(
        (shape = actual && version = User.SnapshotPolicy.SchemaVersion),
        $"User's shape no longer matches pin v{version} (policy is v{User.SnapshotPolicy.SchemaVersion}). "
        + "Bump User.SnapshotPolicy.SchemaVersion and append a pin with the new shape:\n" + actual)

[<Fact>]
let ``User.Fold behaviour and snapshot JSON are pinned to the current snapshot SchemaVersion`` () =
    let version, expected = List.last pinnedFolds
    let actual = foldTranscript ()
    Assert.True(
        (expected = actual && version = User.SnapshotPolicy.SchemaVersion),
        $"User.Fold / snapshot JSON no longer match pin v{version} (policy is v{User.SnapshotPolicy.SchemaVersion}). "
        + "Bump User.SnapshotPolicy.SchemaVersion and append a pin with the new transcript:\n" + actual)

[<Fact>]
let ``pins use strictly increasing schema versions`` () =
    for pins in [ List.map fst pinnedShapes; List.map fst pinnedFolds ] do
        Assert.Equal<int list>(List.sort (List.distinct pins), pins)

[<Fact>]
let ``every intermediate User state survives the snapshot JSON round-trip`` () =
    for k in 0 .. canonicalEvents.Length do
        let state = foldAll User.Zero (List.take k canonicalEvents)
        Assert.Equal(state, roundTrip state)

[<Fact>]
let ``snapshot plus tail equals full replay at every split point`` () =
    let full = foldAll User.Zero canonicalEvents
    for k in 0 .. canonicalEvents.Length do
        let snapshot = roundTrip (foldAll User.Zero (List.take k canonicalEvents))
        Assert.Equal(full, foldAll snapshot (List.skip k canonicalEvents))
