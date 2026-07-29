module VahterBanBot.Tests.SpamTextCacheTests

open System
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

/// Ban-seeded spam-text cache (SPAM_TEXT_CACHE_MODE) integration tests.
///
/// Isolation: the cache is in-process and keyed purely on normalized text, with no per-test
/// Clear() call (ISpamTextCache.Clear exists for callers that need it, but every text used below
/// embeds a fresh GUID — collision with any other test, in this file or any other sharing these
/// containers, is not possible). This mirrors ReactionSpamTests.fs's `svetla_{guid}` convention
/// rather than the truncate-a-table approach LlmVerdictCacheGlobalFlagTests/OcrCacheTests use,
/// because this cache has no table to truncate (see the module doc in SpamTextCache.fs).
let private longSpamText () =
    $"click this link right now to claim your huge prize before it expires forever {Guid.NewGuid()}"

type SpamTextCacheEnforceTests(fixture: SpamTextCacheEnforceTestContainers, _unused: MlAwaitFixture) =

    [<Fact>]
    let ``Enforce mode deletes an exact repeat of a banned message without calling ML or the LLM`` () = task {
        let spamText = longSpamText ()
        let vahter = fixture.Vahters[0]

        // Original spam message, banned by a vahter (seeds the cache).
        let originalMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = spamText)
        let! _ = fixture.SendMessage originalMsg
        let! _ = Tg.replyMsg(originalMsg.Message.Value, "/ban", vahter) |> fixture.SendMessage
        let! userBanned = fixture.UserBanned originalMsg.Message.Value.From.Value.Id
        Assert.True(userBanned, "Sanity: original spammer should be banned")

        // A DIFFERENT user repeats the exact same text.
        let repeatUser = Tg.user()
        let repeatMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = spamText, from = repeatUser)
        let! _ = fixture.SendMessage repeatMsg

        let! wasDeleted = fixture.MessageIsAutoDeleted repeatMsg.Message.Value
        Assert.True(wasDeleted, "Repeat of a just-banned message should be deleted in enforce mode")

        // Short-circuit proof: no MlScoredMessage event (ML never ran) and no LlmClassified
        // event (LLM never ran) for the repeat message.
        let! mlScore = fixture.GetMlScore repeatMsg.Message.Value
        Assert.True(mlScore.IsNone, "Cache hit should short-circuit before ML scoring")
        let! llmVerdict = fixture.TryGetLlmTriageVerdict repeatMsg.Message.Value
        Assert.Equal(None, llmVerdict)
    }

    [<Fact>]
    let ``Seed hygiene: banning a message under 40 normalized chars does not seed the cache`` () = task {
        let shortText = $"spam {Guid.NewGuid().ToString().[..4]}"   // ~10 chars, well under the 40-char floor
        let vahter = fixture.Vahters[0]
        let originalMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = shortText)
        let! _ = fixture.SendMessage originalMsg
        let! _ = Tg.replyMsg(originalMsg.Message.Value, "/ban", vahter) |> fixture.SendMessage

        let repeatUser = Tg.user()
        let repeatMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = shortText, from = repeatUser)
        let! _ = fixture.SendMessage repeatMsg

        // Not short-circuited: the repeat goes through the normal (ML) pipeline, proving the
        // too-short ban never seeded the cache.
        let! mlScore = fixture.GetMlScore repeatMsg.Message.Value
        Assert.True(mlScore.IsSome, "Text under the seed-hygiene floor must not seed the cache")
    }

    [<Fact>]
    let ``TTL expiry: a repeat past the TTL is no longer caught`` () = task {
        let spamText = longSpamText ()
        let vahter = fixture.Vahters[0]
        let originalMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = spamText)
        let! _ = fixture.SendMessage originalMsg
        let! _ = Tg.replyMsg(originalMsg.Message.Value, "/ban", vahter) |> fixture.SendMessage

        // Sanity: a fresh repeat right after the ban is still caught (default TTL is 24h).
        let repeatUser1 = Tg.user()
        let repeatMsg1 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = spamText, from = repeatUser1)
        let! _ = fixture.SendMessage repeatMsg1
        let! wasDeleted1 = fixture.MessageIsAutoDeleted repeatMsg1.Message.Value
        Assert.True(wasDeleted1, "Sanity: repeat within TTL should be caught")

        // Jump the clock past the default 24h TTL (SPAM_TEXT_CACHE_TTL_HOURS), then repeat again
        // from a third, fresh sender.
        let future = DateTime.UtcNow.AddHours(25.0).ToString("o")
        do! fixture.SetBotSetting("BOT_FIXED_UTC_NOW", future)
        do! fixture.ReloadSettings()

        let repeatUser2 = Tg.user()
        let repeatMsg2 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = spamText, from = repeatUser2)
        let! _ = fixture.SendMessage repeatMsg2
        let! mlScore2 = fixture.GetMlScore repeatMsg2.Message.Value
        Assert.True(mlScore2.IsSome, "Past the TTL, the repeat must go through normal ML scoring again")

        // Revert to the system clock so later tests aren't affected (same convention as
        // MLBanTests's "Expired ban does not suppress messages").
        do! fixture.SetBotSetting("BOT_FIXED_UTC_NOW", "")
        do! fixture.ReloadSettings()
    }

    [<Fact>]
    let ``Normalization matches across case and whitespace differences`` () = task {
        let unique = string (Guid.NewGuid())
        let bannedText = $"  CLICK   this   LINK right NOW to Claim your Huge Prize {unique}  "
        let repeatText = $"click this link right now to claim your huge prize {unique}"
        let vahter = fixture.Vahters[0]
        let originalMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = bannedText)
        let! _ = fixture.SendMessage originalMsg
        let! _ = Tg.replyMsg(originalMsg.Message.Value, "/ban", vahter) |> fixture.SendMessage

        let repeatUser = Tg.user()
        let repeatMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = repeatText, from = repeatUser)
        let! _ = fixture.SendMessage repeatMsg

        let! wasDeleted = fixture.MessageIsAutoDeleted repeatMsg.Message.Value
        Assert.True(wasDeleted, "Case/whitespace-only differences must still match")
    }

    [<Fact>]
    let ``Normalization does not strip digits -- different digits do not match`` () = task {
        let unique = string (Guid.NewGuid())
        let bannedText = $"your verification code is 483920 please confirm now urgently {unique}"
        let repeatText = $"your verification code is 172645 please confirm now urgently {unique}"
        let vahter = fixture.Vahters[0]
        let originalMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = bannedText)
        let! _ = fixture.SendMessage originalMsg
        let! _ = Tg.replyMsg(originalMsg.Message.Value, "/ban", vahter) |> fixture.SendMessage

        let repeatUser = Tg.user()
        let repeatMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = repeatText, from = repeatUser)
        let! _ = fixture.SendMessage repeatMsg

        let! mlScore = fixture.GetMlScore repeatMsg.Message.Value
        Assert.True(mlScore.IsSome, "A different digit sequence must NOT match -- digits are not stripped by normalization")
    }

    [<Fact>]
    let ``Normalization does not strip URLs -- different URLs do not match`` () = task {
        let unique = string (Guid.NewGuid())
        let bannedText = $"click here to claim your prize http://spam-example-one.test/prize {unique}"
        let repeatText = $"click here to claim your prize http://spam-example-two.test/prize {unique}"
        let vahter = fixture.Vahters[0]
        let originalMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = bannedText)
        let! _ = fixture.SendMessage originalMsg
        let! _ = Tg.replyMsg(originalMsg.Message.Value, "/ban", vahter) |> fixture.SendMessage

        let repeatUser = Tg.user()
        let repeatMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = repeatText, from = repeatUser)
        let! _ = fixture.SendMessage repeatMsg

        let! mlScore = fixture.GetMlScore repeatMsg.Message.Value
        Assert.True(mlScore.IsSome, "A different URL must NOT match -- URLs are not stripped by normalization")
    }

    [<Fact>]
    let ``Old-user immunity: a cache hit is not actioned for a user with many prior messages`` () = task {
        let spamText = longSpamText ()
        let vahter = fixture.Vahters[0]

        // Seed the cache via a fresh spammer's ban.
        let originalMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = spamText)
        let! _ = fixture.SendMessage originalMsg
        let! _ = Tg.replyMsg(originalMsg.Message.Value, "/ban", vahter) |> fixture.SendMessage

        // Build an "old" user: 10 unique harmless prior messages. ML_OLD_USER_MSG_COUNT is 10 in
        // test settings (see ContainerTestBase.fs's mlSettings), same threshold this cache reuses.
        let oldUser = Tg.user(firstName = "old trusted user")
        for text in ["p1"; "p2"; "p3"; "p4"; "p5"; "p6"; "p7"; "p8"; "p9"; "p10"] do
            let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = text, from = oldUser)
            let! _ = fixture.SendMessage msg
            ()

        // The old user repeats the exact banned text.
        let repeatMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = spamText, from = oldUser)
        let! _ = fixture.SendMessage repeatMsg

        let! wasDeleted = fixture.MessageIsAutoDeleted repeatMsg.Message.Value
        Assert.False(wasDeleted, "Old-user immunity must suppress the cache-hit action")

        // Proof this is immunity, not a cache miss: ML DID run normally for this message, same
        // as ML's own old-user immunity (mlScore recorded, then immunity applies downstream).
        let! mlScore = fixture.GetMlScore repeatMsg.Message.Value
        Assert.True(mlScore.IsSome, "Old-user immunity falls through to normal ML scoring")
    }

    [<Fact>]
    let ``Eligibility: a vahter's own message is not affected by the cache`` () = task {
        let spamText = longSpamText ()
        let vahter = fixture.Vahters[0]
        let originalMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = spamText)
        let! _ = fixture.SendMessage originalMsg
        let! _ = Tg.replyMsg(originalMsg.Message.Value, "/ban", vahter) |> fixture.SendMessage

        // The SAME vahter posts the exact banned text as a normal (non-command) message.
        let vahterMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = spamText, from = vahter)
        let! _ = fixture.SendMessage vahterMsg

        let! wasDeleted = fixture.MessageIsAutoDeleted vahterMsg.Message.Value
        Assert.False(wasDeleted, "A vahter's own message must never be actioned, cache hit or not")
    }

    [<Fact>]
    let ``Startup rehydration query returns manual bans with text, excluding auto-bans and out-of-window bans`` () = task {
        let spamText = longSpamText ()
        let vahter = fixture.Vahters[0]
        let beforeBan = DateTime.UtcNow

        let originalMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = spamText)
        let! _ = fixture.SendMessage originalMsg
        let! _ = Tg.replyMsg(originalMsg.Message.Value, "/ban", vahter) |> fixture.SendMessage

        // A wide window (since = just before the ban) includes the manual ban with its text --
        // this is exactly the query Program.fs runs on startup to rehydrate the in-process cache.
        let! seedsWide = fixture.GetRecentManualBansWithText(beforeBan.AddSeconds(-5.0))
        let seed =
            seedsWide
            |> Array.tryFind (fun s ->
                s.chat_id = originalMsg.Message.Value.Chat.Id && s.message_id = originalMsg.Message.Value.MessageId)
        Assert.True(seed.IsSome, "Recent manual ban with text should be returned by the rehydration query")
        Assert.Equal(spamText, seed.Value.message_text)

        // A `since` cutoff placed AFTER the ban excludes it -- simulates a restart far enough in
        // the future that the ban has aged out of the TTL window.
        let! seedsNarrow = fixture.GetRecentManualBansWithText(DateTime.UtcNow.AddSeconds(5.0))
        let stillThere =
            seedsNarrow
            |> Array.exists (fun s ->
                s.chat_id = originalMsg.Message.Value.Chat.Id && s.message_id = originalMsg.Message.Value.MessageId)
        Assert.False(stillThere, "A since-cutoff after the ban must exclude it")

        // Auto-bans must never appear -- trigger one via the existing karma-autoban path (4
        // consecutive spam messages hits the karma threshold, same as MLBanTests).
        let autoBanSpammer = Tg.user()
        for _ in 1..4 do
            let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222", from = autoBanSpammer)
            let! _ = fixture.SendMessage msg
            ()
        let! autoBanned = fixture.UserBannedByBot autoBanSpammer.Id
        Assert.True(autoBanned, "Sanity: spammer should be auto-banned by the karma system")

        let! seedsAfterAutoBan = fixture.GetRecentManualBansWithText(beforeBan.AddSeconds(-5.0))
        let autoBanLeaked =
            seedsAfterAutoBan
            |> Array.exists (fun s -> s.message_text = "2222222")
        Assert.False(autoBanLeaked, "Auto-bans (actor <> User) must never be returned by the rehydration query")
    }

    interface IClassFixture<MlAwaitFixture>

type SpamTextCacheShadowTests(fixture: SpamTextCacheShadowTestContainers, _unused: MlAwaitFixture) =

    [<Fact>]
    let ``Shadow mode reports an exact repeat without deleting it`` () = task {
        let spamText = longSpamText ()
        let vahter = fixture.Vahters[0]
        let originalMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = spamText)
        let! _ = fixture.SendMessage originalMsg
        let! _ = Tg.replyMsg(originalMsg.Message.Value, "/ban", vahter) |> fixture.SendMessage

        let repeatUser = Tg.user()
        let repeatMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = spamText, from = repeatUser)
        let! _ = fixture.SendMessage repeatMsg

        let! wasDeleted = fixture.MessageIsAutoDeleted repeatMsg.Message.Value
        Assert.False(wasDeleted, "Shadow mode must never delete")

        // Reported to the Potential Spam channel: ReportPotentialSpam always creates a "Spam"
        // (KILL) callback there. GetCallbackId retries briefly and throws if never found, so
        // reaching the next line is itself the assertion.
        let! _ = fixture.GetCallbackId repeatMsg.Message.Value "Spam"

        let! mlScore = fixture.GetMlScore repeatMsg.Message.Value
        Assert.True(mlScore.IsNone, "Cache hit should short-circuit before ML scoring, even in shadow mode")
    }

    interface IClassFixture<MlAwaitFixture>

/// Uses the DEFAULT shared ML-enabled container, which never sets SPAM_TEXT_CACHE_MODE (so it's
/// "off" per Program.fs's default), proving the feature is inert unless explicitly enabled.
type SpamTextCacheOffTests(fixture: MlEnabledVahterTestContainers, _unused: MlAwaitFixture) =

    [<Fact>]
    let ``Off mode (default) leaves the cache inert`` () = task {
        let spamText = longSpamText ()
        let vahter = fixture.Vahters[0]
        let originalMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = spamText)
        let! _ = fixture.SendMessage originalMsg
        let! _ = Tg.replyMsg(originalMsg.Message.Value, "/ban", vahter) |> fixture.SendMessage

        let repeatUser = Tg.user()
        let repeatMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = spamText, from = repeatUser)
        let! _ = fixture.SendMessage repeatMsg

        // Proves inertness precisely: the pipeline ran ML normally (not short-circuited), which
        // is exactly what would NOT happen if the cache had (incorrectly) matched in Off mode.
        let! mlScore = fixture.GetMlScore repeatMsg.Message.Value
        Assert.True(mlScore.IsSome, "With the cache off, the repeat must go through normal ML scoring")
    }

    interface IClassFixture<MlAwaitFixture>
