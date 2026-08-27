module VahterBanBot.Tests.SpamTextCacheTests

open System
open VahterBanBot.SpamTextCache
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

/// Ban-seeded spam-text cache (SPAM_TEXT_CACHE_MODE) integration tests.
///
/// Isolation: Postgres-backed but keyed on normalized text; no per-test Clear() since every text
/// below embeds a fresh GUID (ReactionSpamTests.fs's convention, not a truncate-a-table approach).
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

    /// Two independent `SpamTextCache` instances simulate two pods: a seed written by one must
    /// be visible to a lookup on the other -- the property a ConcurrentDictionary could never fix.
    [<Fact>]
    let ``Cross-pod visibility: a seed written via one SpamTextCache instance is read via a second`` () = task {
        let text = $"win a free prize right now claim it before it expires forever {Guid.NewGuid()}"
        let podA = SpamTextCache(fixture.DbConnectionString) :> ISpamTextCache
        let podB = SpamTextCache(fixture.DbConnectionString) :> ISpamTextCache
        let bannedAt = DateTime.UtcNow

        let! seeded = podA.Seed(text, 40, TimeSpan.FromHours 24.0, -666L, 1L, bannedAt)
        Assert.True(seeded, "Seed should succeed for text over the min-length floor")

        let! hit = podB.TryGet(text, bannedAt)
        Assert.True(hit.IsSome, "A seed written by one instance must be visible to a lookup on a different instance")
        Assert.Equal(-666L, hit.Value.SeedChatId)
        Assert.Equal(1L, hit.Value.SeedMessageId)
    }

    /// Same two-instance setup, but proves TTL expiry is enforced by the read, not writer-side
    /// eviction -- a pod that never wrote the seed still sees it expire correctly.
    [<Fact>]
    let ``Cross-pod TTL: expiry is enforced on read by a different instance than the one that seeded`` () = task {
        let text = $"limited time offer act now before this deal disappears forever {Guid.NewGuid()}"
        let podA = SpamTextCache(fixture.DbConnectionString) :> ISpamTextCache
        let podB = SpamTextCache(fixture.DbConnectionString) :> ISpamTextCache
        let bannedAt = DateTime.UtcNow

        let! _ = podA.Seed(text, 40, TimeSpan.FromHours 1.0, -666L, 2L, bannedAt)

        let! hitBeforeExpiry = podB.TryGet(text, bannedAt.AddMinutes 30.0)
        Assert.True(hitBeforeExpiry.IsSome, "Lookup within the TTL window should hit")

        let! hitAfterExpiry = podB.TryGet(text, bannedAt.AddHours 2.0)
        Assert.True(hitAfterExpiry.IsNone, "Lookup past the TTL window should miss")
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
