module VahterBanBot.Tests.SpamWarningTests

open System
open System.Threading.Tasks
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

/// Ephemeral warning sent to a user whose message was just auto-deleted as spam (Bot API 10.2,
/// DeleteSpam). SPAM_WARNING_ENABLED defaults to false; tests that need it flip the bot_setting
/// and DisposeAsync restores it after every test — same pattern as EphemeralTests/ReportCommandTests.
type SpamWarningMlTests(fixture: MlEnabledVahterTestContainers, _unused: MlAwaitFixture) =

    let setSpamWarning (enabled: bool) = task {
        do! fixture.SetBotSetting("SPAM_WARNING_ENABLED", if enabled then "true" else "false")
        do! fixture.ReloadSettings()
    }

    [<Fact>]
    let ``Flag ON: ML spam deletion of a non-banned user sends exactly one ephemeral warning`` () = task {
        do! setSpamWarning true

        let user = Tg.user()
        do! fixture.ClearFakeCalls()
        // "2222222" is a training-set spam word (see test_seed.sql), scoring 1.5686... under the
        // fixture ML model (see MLScoreDeterminismTests) — comfortably below the default
        // SPAM_WARNING_MAX_SCORE of 3.0. A single message never crosses the karma-autoban
        // threshold on its own.
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222", from = user)
        let! _ = fixture.SendMessage msgUpdate

        let! msgDeleted = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.True(msgDeleted, "Sanity: message should have been auto-deleted as spam")
        let! userBanned = fixture.UserBannedByBot user.Id
        Assert.False(userBanned, "Sanity: a single spam message must not trigger karma autoban")

        let! calls = fixture.GetFakeCalls "sendMessage"
        let warnings =
            calls
            |> Array.filter (fun c ->
                c.Body.Contains $"\"chat_id\":{fixture.ChatsToMonitor[0].Id}"
                && c.Body.Contains $"\"receiver_user_id\":{user.Id}")
        Assert.Equal(1, warnings.Length)
        Assert.Contains("automatically", warnings[0].Body)
    }

    [<Fact>]
    let ``Flag ON: ML spam deletion scoring at/above SPAM_WARNING_MAX_SCORE sends no warning`` () = task {
        do! setSpamWarning true

        // Probe the exact ML score for this text under a deliberately permissive cutoff, then
        // set SPAM_WARNING_MAX_SCORE just below it — robust against the underlying ML model
        // (and its score scale) changing under retraining, unlike hardcoding a score value.
        do! fixture.SetBotSetting("SPAM_WARNING_MAX_SCORE", "999")
        do! fixture.ReloadSettings()
        let probeMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222", from = Tg.user())
        let! _ = fixture.SendMessage probeMsg
        let! scoreOpt = fixture.GetMlScore probeMsg.Message.Value
        let score =
            match scoreOpt with
            | Some s -> s
            | None -> failwith "Sanity: probe message should have been ML-scored"

        do! fixture.SetBotSetting("SPAM_WARNING_MAX_SCORE", string (score - 0.5))
        do! fixture.ReloadSettings()
        let user = Tg.user()
        do! fixture.ClearFakeCalls()
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222", from = user)
        let! _ = fixture.SendMessage msgUpdate

        let! msgDeleted = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.True(msgDeleted, "Sanity: message should still be auto-deleted regardless of the warning cutoff")

        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.False(
            calls |> Array.exists (fun c -> c.Body.Contains $"\"receiver_user_id\":{user.Id}"),
            "a deletion scoring at/above SPAM_WARNING_MAX_SCORE must not send the ephemeral warning")
    }

    [<Fact>]
    let ``Flag ON: boundary — score exactly equal to SPAM_WARNING_MAX_SCORE sends no warning`` () = task {
        do! setSpamWarning true

        // Same probe-then-pin approach as the at/above-cutoff test, but this time the cutoff is
        // pinned to the *exact* observed score (round-trippable via .NET's default double
        // ToString()), exercising the strict `<` boundary rather than a margin below it.
        do! fixture.SetBotSetting("SPAM_WARNING_MAX_SCORE", "999")
        do! fixture.ReloadSettings()
        let probeMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222", from = Tg.user())
        let! _ = fixture.SendMessage probeMsg
        let! scoreOpt = fixture.GetMlScore probeMsg.Message.Value
        let score =
            match scoreOpt with
            | Some s -> s
            | None -> failwith "Sanity: probe message should have been ML-scored"

        do! fixture.SetBotSetting("SPAM_WARNING_MAX_SCORE", string score)
        do! fixture.ReloadSettings()
        let user = Tg.user()
        do! fixture.ClearFakeCalls()
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222", from = user)
        let! _ = fixture.SendMessage msgUpdate

        let! msgDeleted = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.True(msgDeleted, "Sanity: message should still be auto-deleted at the boundary")

        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.False(
            calls |> Array.exists (fun c -> c.Body.Contains $"\"receiver_user_id\":{user.Id}"),
            "score == SPAM_WARNING_MAX_SCORE must not warn -- the cutoff is a strict less-than")
    }

    [<Fact>]
    let ``Flag OFF (default): no warning is sent for the same scenario`` () = task {
        do! setSpamWarning false

        let user = Tg.user()
        do! fixture.ClearFakeCalls()
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222", from = user)
        let! _ = fixture.SendMessage msgUpdate

        let! msgDeleted = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.True(msgDeleted, "Sanity: message should still be auto-deleted with the flag off")

        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.False(
            calls |> Array.exists (fun c -> c.Body.Contains $"\"receiver_user_id\":{user.Id}"),
            "no ephemeral warning must be sent while SPAM_WARNING_ENABLED is false")
    }

    [<Fact>]
    let ``Flag ON: the deletion that triggers karma autoban sends no warning`` () = task {
        do! setSpamWarning true

        // Same "66666666" / 4-consecutive-spam karma-autoban shape as MLBanTests.
        let user = Tg.user()
        let spam = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "66666666", from = user)
        for _ in 1..3 do
            let! _ = fixture.SendMessage spam
            ()
        let! bannedYet = fixture.UserBannedByBot user.Id
        Assert.False(bannedYet, "Sanity: user should not be auto-banned after only 3 spam messages")

        do! fixture.ClearFakeCalls()
        let! _ = fixture.SendMessage spam
        let! banned = fixture.UserBannedByBot user.Id
        Assert.True(banned, "Sanity: 4th spam message should trigger karma autoban")

        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.False(
            calls |> Array.exists (fun c -> c.Body.Contains $"\"receiver_user_id\":{user.Id}"),
            "a deletion that triggers total-ban must not also send an ephemeral warning")
    }

    // Restore the flag and cutoff to their defaults after every test.
    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetBotSetting("SPAM_WARNING_ENABLED", "false")
                do! fixture.SetBotSetting("SPAM_WARNING_MAX_SCORE", "3.0")
                do! fixture.ReloadSettings()
            } :> Task)

    interface IClassFixture<MlAwaitFixture>

/// SpamTextCacheHit is one of the excluded reasons (near-zero-false-positive spammer
/// signatures) — a warning there would only educate spammers.
type SpamWarningCacheHitTests(fixture: SpamTextCacheEnforceTestContainers, _unused: MlAwaitFixture) =

    let setSpamWarning (enabled: bool) = task {
        do! fixture.SetBotSetting("SPAM_WARNING_ENABLED", if enabled then "true" else "false")
        do! fixture.ReloadSettings()
    }

    [<Fact>]
    let ``Flag ON: a ban-seeded spam-text cache hit deletion sends no warning`` () = task {
        do! setSpamWarning true

        let spamText = $"click this link right now to claim your huge prize before it expires forever {System.Guid.NewGuid()}"
        let vahter = fixture.Vahters[0]
        let originalMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = spamText)
        let! _ = fixture.SendMessage originalMsg
        let! _ = Tg.replyMsg(originalMsg.Message.Value, "/ban", vahter) |> fixture.SendMessage
        let! seedUserBanned = fixture.UserBanned originalMsg.Message.Value.From.Value.Id
        Assert.True(seedUserBanned, "Sanity: original spammer should be banned, seeding the cache")

        let repeatUser = Tg.user()
        do! fixture.ClearFakeCalls()
        let repeatMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = spamText, from = repeatUser)
        let! _ = fixture.SendMessage repeatMsg

        let! wasDeleted = fixture.MessageIsAutoDeleted repeatMsg.Message.Value
        Assert.True(wasDeleted, "Sanity: repeat of a just-banned message should be deleted (enforce mode)")

        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.False(
            calls |> Array.exists (fun c -> c.Body.Contains $"\"receiver_user_id\":{repeatUser.Id}"),
            "SpamTextCacheHit deletions must never send the ephemeral warning")
    }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetBotSetting("SPAM_WARNING_ENABLED", "false")
                do! fixture.ReloadSettings()
            } :> Task)

    interface IClassFixture<MlAwaitFixture>

/// The "already banned" fast-delete path (JustMessage) has its own deletion code and never
/// calls DeleteSpam at all — this locks that finding in as a regression test.
type SpamWarningAlreadyBannedTests(fixture: MlDisabledVahterTestContainers) =

    let setSpamWarning (enabled: bool) = task {
        do! fixture.SetBotSetting("SPAM_WARNING_ENABLED", if enabled then "true" else "false")
        do! fixture.ReloadSettings()
    }

    [<Fact>]
    let ``Flag ON: a late message from an already-banned user sends no warning`` () = task {
        do! setSpamWarning true

        let spammer = Tg.user()
        let firstMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], from = spammer)
        let! _ = fixture.SendMessage firstMsg
        let! _ = Tg.replyMsg(firstMsg.Message.Value, "/ban", fixture.Vahters[0]) |> fixture.SendMessage
        let! banned = fixture.MessageBanned firstMsg.Message.Value
        Assert.True(banned, "Sanity: spammer should be banned by the manual /ban")

        do! fixture.ClearFakeCalls()
        let secondMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], from = spammer)
        let! _ = fixture.SendMessage secondMsg

        let! deleteCalls = fixture.GetFakeCalls "deleteMessage"
        Assert.True(
            deleteCalls |> Array.exists (fun c -> c.Body.Contains $"\"message_id\":{secondMsg.Message.Value.MessageId}"),
            "Sanity: the late message from an already-banned user should still be deleted")

        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.False(
            calls |> Array.exists (fun c -> c.Body.Contains $"\"receiver_user_id\":{spammer.Id}"),
            "the already-banned fast-delete path (JustMessage) never goes through DeleteSpam and must never warn")
    }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetBotSetting("SPAM_WARNING_ENABLED", "false")
                do! fixture.ReloadSettings()
            } :> Task)
