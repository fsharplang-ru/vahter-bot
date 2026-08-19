module VahterBanBot.Tests.SpamProtectionTests

open System
open System.Text.Json
open System.Threading.Tasks
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

/// Channel posts can be fire-and-forget, so poll for the matching sendMessage call instead of
/// asserting on a single snapshot — same helper as MLBanTests.fs's tryFindChannelPost.
let private tryFindChannelPost (fixture: MlEnabledVahterTestContainers) (channelId: int64) (marker: string) = task {
    let mutable found = None
    let mutable attempts = 0
    while found.IsNone && attempts < 40 do
        let! calls = fixture.GetFakeCalls "sendMessage"
        found <- calls |> Array.tryFind (fun c ->
            c.Body.Contains $"\"chat_id\":{channelId}" && c.Body.Contains marker)
        if found.IsNone then
            attempts <- attempts + 1
            do! Task.Delay 250
    return found
}

let private setSpamProtection (fixture: MlEnabledVahterTestContainers) (enabled: bool) = task {
    do! fixture.SetBotSetting("SPAM_PROTECTION_ENABLED", if enabled then "true" else "false")
    do! fixture.ReloadSettings()
}

let private resetSpamProtectionSettings (fixture: MlEnabledVahterTestContainers) = task {
    do! fixture.SetBotSetting("SPAM_PROTECTION_ENABLED", "false")
    do! fixture.SetBotSetting("SPAM_PROTECTION_MAX_HITS", "5")
    do! fixture.SetBotSetting("SPAM_PROTECTION_NOTIFY_ENABLED", "false")
    do! fixture.SetBotSetting("SPAM_WARNING_ENABLED", "false")
    do! fixture.ReloadSettings()
}

/// Test item 1 (grant on ham-mark, both entry points) + item 8 (flag off = today's behavior).
type SpamProtectionGrantTests(fixture: MlEnabledVahterTestContainers, _unused: MlAwaitFixture) =

    [<Fact>]
    let ``Flag ON: NotASpam button click on an auto-deleted message grants spam protection`` () = task {
        do! setSpamProtection fixture true

        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222")
        let! _ = fixture.SendMessage msgUpdate
        let! deleted = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.True(deleted, "Sanity: message should have been auto-deleted as spam")

        let userId = msgUpdate.Message.Value.From.Value.Id
        let! callbackId = fixture.GetCallbackId msgUpdate.Message.Value "NotASpam"
        let! _ = fixture.SendMessage(Tg.callback(string callbackId, from = fixture.Vahters[0]))

        let! granted = fixture.SpamProtectionGranted userId
        Assert.True(granted, "SpamProtectionGranted event should be recorded")

        let! snap = fixture.TryGetSnapshotUser userId
        Assert.True(snap.IsSome)
        Assert.True(snap.Value.spam_protection_until.HasValue, "snapshot_user.spam_protection_until should be populated")
        Assert.Equal(0, snap.Value.spam_protection_hits.Value)
    }

    [<Fact>]
    let ``Flag ON: /vahter unmarkspam on an auto-deleted message grants spam protection`` () = task {
        do! setSpamProtection fixture true
        // Isolate from any "Deleted spam" post left behind by another Fact sharing this class's
        // fixture — tryFindChannelPost below must only ever match THIS test's own message.
        do! fixture.ClearFakeCalls()

        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222")
        let! _ = fixture.SendMessage msgUpdate
        let! deleted = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.True(deleted, "Sanity: message should have been auto-deleted as spam")

        let chatId = msgUpdate.Message.Value.Chat.Id
        let messageId = msgUpdate.Message.Value.MessageId
        let userId = msgUpdate.Message.Value.From.Value.Id

        // Forward the AllLogs "Deleted spam" post into the admin channel and reply /vahter
        // unmarkspam to it — same recovery flow as MLBanTests' markspam re-mark test.
        let! logPost = tryFindChannelPost fixture fixture.AllLogsChannel.Id "Deleted spam"
        Assert.True(logPost.IsSome)
        let logText = JsonDocument.Parse(logPost.Value.Body).RootElement.GetProperty("text").GetString()
        let forwarded = Tg.quickMsg(text = logText, chat = fixture.AdminChannel, from = fixture.Vahters[0])
        let! resp =
            Tg.replyMsg(forwarded.Message.Value, "/vahter unmarkspam", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode)

        let! ham = fixture.MessageMarkedHam(chatId, messageId)
        Assert.True(ham, "Sanity: /vahter unmarkspam should record MessageMarkedHam")
        let! granted = fixture.SpamProtectionGranted userId
        Assert.True(granted, "SpamProtectionGranted event should be recorded via the /vahter unmarkspam entry point")
    }

    [<Fact>]
    let ``Flag OFF (default): behavior identical to today — no grant, repeated spam keeps auto-deleting`` () = task {
        do! setSpamProtection fixture false

        let user = Tg.user()
        let spamUpdate () = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222", from = user)

        let update1 = spamUpdate ()
        let! _ = fixture.SendMessage update1
        let! deleted1 = fixture.MessageIsAutoDeleted update1.Message.Value
        Assert.True(deleted1, "Sanity: first spam message should be auto-deleted")

        let! callbackId = fixture.GetCallbackId update1.Message.Value "NotASpam"
        let! _ = fixture.SendMessage(Tg.callback(string callbackId, from = fixture.Vahters[0]))

        let! granted = fixture.SpamProtectionGranted user.Id
        Assert.False(granted, "No SpamProtectionGranted event while the flag is off")

        // Behavior unaffected: the next spam message from the same (now ham-marked-once) user
        // still auto-deletes normally — no demotion machinery engaged.
        let update2 = spamUpdate ()
        let! _ = fixture.SendMessage update2
        let! deleted2 = fixture.MessageIsAutoDeleted update2.Message.Value
        Assert.True(deleted2, "Flag off: subsequent spam must keep auto-deleting exactly as before this feature")
    }

    interface IAsyncDisposable with
        member _.DisposeAsync() = ValueTask(resetSpamProtectionSettings fixture :> Task)

    interface IClassFixture<MlAwaitFixture>

/// Test item 2: a protected user's would-be ML spam deletion is demoted to report-only.
type SpamProtectionDemotionTests(fixture: MlEnabledVahterTestContainers, _unused: MlAwaitFixture) =

    [<Fact>]
    let ``Protected user + ML spam verdict is demoted: no delete, no BotAutoDeleted, tagged Potential Spam card, budget consumed`` () = task {
        do! setSpamProtection fixture true

        let user = Tg.user()
        let spamUpdate () = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222", from = user)

        // First message: not protected yet — deletes normally and seeds the grant via NotASpam.
        let update1 = spamUpdate ()
        let! _ = fixture.SendMessage update1
        let! deleted1 = fixture.MessageIsAutoDeleted update1.Message.Value
        Assert.True(deleted1, "Sanity: first spam message should be auto-deleted")
        let! callbackId = fixture.GetCallbackId update1.Message.Value "NotASpam"
        let! _ = fixture.SendMessage(Tg.callback(string callbackId, from = fixture.Vahters[0]))
        let! granted = fixture.SpamProtectionGranted user.Id
        Assert.True(granted, "Sanity: protection should now be granted")

        do! fixture.ClearFakeCalls()

        // Second message: now protected — must be demoted, not deleted.
        let update2 = spamUpdate ()
        let! _ = fixture.SendMessage update2
        let! deleted2 = fixture.MessageIsAutoDeleted update2.Message.Value
        Assert.False(deleted2, "A protected user's would-be spam deletion must be demoted, not deleted")

        // ASCII-only marker: the 🛡 emoji round-trips through JSON as a \u-escaped surrogate
        // pair in the raw HTTP body, so a literal-emoji Contains check on c.Body would never match.
        let! card = tryFindChannelPost fixture fixture.PotentialSpamChannel.Id "protected user"
        Assert.True(card.IsSome, "Demoted message should post a Potential Spam card tagged with the protected-user marker")

        let! consumed = fixture.SpamProtectionConsumedCount user.Id
        Assert.Equal(1, consumed)
    }

    interface IAsyncDisposable with
        member _.DisposeAsync() = ValueTask(resetSpamProtectionSettings fixture :> Task)

    interface IClassFixture<MlAwaitFixture>

/// Test item 3: budget exhaustion revokes protection and lets the exceeding message delete
/// normally, including the existing #395 ephemeral-warning behavior on that deletion.
type SpamProtectionBudgetTests(fixture: MlEnabledVahterTestContainers, _unused: MlAwaitFixture) =

    [<Fact>]
    let ``Budget exhaustion: the demotion beyond SPAM_PROTECTION_MAX_HITS revokes and deletes normally (warning unaffected)`` () = task {
        do! fixture.SetBotSetting("SPAM_PROTECTION_ENABLED", "true")
        do! fixture.SetBotSetting("SPAM_PROTECTION_MAX_HITS", "1")
        do! fixture.SetBotSetting("SPAM_WARNING_ENABLED", "true")
        do! fixture.ReloadSettings()

        let user = Tg.user()
        let spamUpdate () = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222", from = user)

        // 1: not protected yet — deletes and grants.
        let update1 = spamUpdate ()
        let! _ = fixture.SendMessage update1
        let! deleted1 = fixture.MessageIsAutoDeleted update1.Message.Value
        Assert.True(deleted1, "Sanity: first spam message should be auto-deleted")
        let! callbackId = fixture.GetCallbackId update1.Message.Value "NotASpam"
        let! _ = fixture.SendMessage(Tg.callback(string callbackId, from = fixture.Vahters[0]))

        // 2: protected, under budget (0 < MaxHits=1) — demoted.
        let update2 = spamUpdate ()
        let! _ = fixture.SendMessage update2
        let! deleted2 = fixture.MessageIsAutoDeleted update2.Message.Value
        Assert.False(deleted2, "Sanity: second message should be demoted (under budget)")
        let! consumed = fixture.SpamProtectionConsumedCount user.Id
        Assert.Equal(1, consumed)

        do! fixture.ClearFakeCalls()

        // 3: budget exhausted (1 >= MaxHits=1) — revoke + normal deletion, warning still fires.
        let update3 = spamUpdate ()
        let! _ = fixture.SendMessage update3
        let! deleted3 = fixture.MessageIsAutoDeleted update3.Message.Value
        Assert.True(deleted3, "Budget-exceeding message must delete normally, not demote again")

        let! reasons = fixture.SpamProtectionRevokedReasons user.Id
        Assert.Contains("budget", reasons)

        let! calls = fixture.GetFakeCalls "sendMessage"
        let warnings =
            calls
            |> Array.filter (fun c ->
                c.Body.Contains $"\"chat_id\":{fixture.ChatsToMonitor[0].Id}"
                && c.Body.Contains $"\"receiver_user_id\":{user.Id}")
        Assert.Equal(1, warnings.Length)
    }

    interface IAsyncDisposable with
        member _.DisposeAsync() = ValueTask(resetSpamProtectionSettings fixture :> Task)

    interface IClassFixture<MlAwaitFixture>

/// Test item 4: SpamTextCacheHit is a carve-out — a protected user's cache-hit deletion is
/// NEVER demoted, even with protection active.
///
/// IMPORTANT: SpamTextCacheEnforceTestContainers is an ASSEMBLY-SHARED fixture (see Program.fs's
/// `[<assembly: AssemblyFixture(typeof<SpamTextCacheEnforceTestContainers>)>]`) — ONE database
/// shared by every test CLASS that takes it as a constructor param (this class, plus
/// SpamTextCacheTests' own SpamTextCacheEnforceTests and SpamWarningTests' SpamWarningCacheHitTests).
/// DB.fs's GetUserStatsByLastNMessages computes `is_ham` by matching MessageMarkedHam.text
/// GLOBALLY (not scoped to chat/user) — ham-marking a fixed, reused literal like "2222222" here
/// would poison SpamTextCacheTests' karma-autoban test for every OTHER user who ever sends that
/// exact text (confirmed root cause of a real CI failure — see PR history). Grant protection via
/// direct event injection (GrantSpamProtection) instead of a real ham-mark round trip, so this
/// test never writes a MessageMarkedHam event for any shared/reused text at all.
type SpamProtectionCarveOutTests(fixture: SpamTextCacheEnforceTestContainers, _unused: MlAwaitFixture) =

    [<Fact>]
    let ``Protected user + spam-text cache hit still deletes normally, no demotion`` () = task {
        do! fixture.SetBotSetting("SPAM_PROTECTION_ENABLED", "true")
        do! fixture.ReloadSettings()

        let spamText = $"click this link right now to claim your huge prize before it expires forever {Guid.NewGuid()}"
        let vahter = fixture.Vahters[0]
        let originalMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = spamText)
        let! _ = fixture.SendMessage originalMsg
        let! _ = Tg.replyMsg(originalMsg.Message.Value, "/ban", vahter) |> fixture.SendMessage
        let! seedUserBanned = fixture.UserBanned originalMsg.Message.Value.From.Value.Id
        Assert.True(seedUserBanned, "Sanity: original spammer should be banned, seeding the cache")

        // Grant protection to a DIFFERENT user directly (no real message, no ham mark) —
        // see the type doc comment for why this must never round-trip through a shared literal.
        let repeatUser = Tg.user()
        let until = DateTime.UtcNow.AddHours(48.0)
        do! fixture.GrantSpamProtection(repeatUser.Id, until, fixture.ChatsToMonitor[0].Id, 0L, vahter.Id, version = 1)
        let! granted = fixture.SpamProtectionGranted repeatUser.Id
        Assert.True(granted, "Sanity: repeatUser should now be protected")

        do! fixture.ClearFakeCalls()

        let repeatMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = spamText, from = repeatUser)
        let! _ = fixture.SendMessage repeatMsg
        let! wasDeleted = fixture.MessageIsAutoDeleted repeatMsg.Message.Value
        Assert.True(wasDeleted, "SpamTextCacheHit carve-out: must delete normally even for a protected user")

        let! consumed = fixture.SpamProtectionConsumedCount repeatUser.Id
        Assert.Equal(0, consumed)
    }

    interface IAsyncDisposable with
        member _.DisposeAsync() = ValueTask(task {
            do! fixture.SetBotSetting("SPAM_PROTECTION_ENABLED", "false")
            do! fixture.ReloadSettings()
        } :> Task)

    interface IClassFixture<MlAwaitFixture>

/// Test item 5: an expired grant (until in the past) behaves as if never granted.
type SpamProtectionExpiryTests(fixture: MlEnabledVahterTestContainers, _unused: MlAwaitFixture) =

    [<Fact>]
    let ``Expired grant: would-be spam deletion proceeds normally, no demotion`` () = task {
        do! setSpamProtection fixture true

        let user = Tg.user()
        let pastUntil = DateTime.UtcNow.AddHours(-1.0)
        do! fixture.GrantSpamProtection(user.Id, pastUntil, fixture.ChatsToMonitor[0].Id, 0L, fixture.Vahters[0].Id, version = 1)

        let update = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222", from = user)
        let! _ = fixture.SendMessage update
        let! deleted = fixture.MessageIsAutoDeleted update.Message.Value
        Assert.True(deleted, "An expired grant must not demote — normal deletion proceeds")

        let! consumed = fixture.SpamProtectionConsumedCount user.Id
        Assert.Equal(0, consumed)
    }

    interface IAsyncDisposable with
        member _.DisposeAsync() = ValueTask(resetSpamProtectionSettings fixture :> Task)

    interface IClassFixture<MlAwaitFixture>

/// Test item 6: KILL on a protected user's demoted card revokes protection and proceeds with
/// the total ban exactly as today.
type SpamProtectionKillTests(fixture: MlEnabledVahterTestContainers, _unused: MlAwaitFixture) =

    [<Fact>]
    let ``KILL on a protected user's demoted card revokes protection and bans as usual`` () = task {
        do! setSpamProtection fixture true

        let user = Tg.user()
        let spamUpdate () = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222", from = user)

        let update1 = spamUpdate ()
        let! _ = fixture.SendMessage update1
        let! deleted1 = fixture.MessageIsAutoDeleted update1.Message.Value
        Assert.True(deleted1, "Sanity: first spam message should be auto-deleted")
        let! callbackId = fixture.GetCallbackId update1.Message.Value "NotASpam"
        let! _ = fixture.SendMessage(Tg.callback(string callbackId, from = fixture.Vahters[0]))
        let! granted = fixture.SpamProtectionGranted user.Id
        Assert.True(granted, "Sanity: protection should now be granted")

        let! bannedBefore = fixture.UserBanned user.Id
        Assert.False(bannedBefore, "Sanity: user should not be banned yet")

        let update2 = spamUpdate ()
        let! _ = fixture.SendMessage update2
        let! deleted2 = fixture.MessageIsAutoDeleted update2.Message.Value
        Assert.False(deleted2, "Sanity: second message should be demoted, not deleted")

        let! killId = fixture.GetCallbackId update2.Message.Value "Spam"
        let! _ = fixture.SendMessage(Tg.callback(string killId, from = fixture.Vahters[0]))

        let! banned = fixture.UserBanned user.Id
        Assert.True(banned, "KILL on a demoted card should ban the user exactly as today")

        let! reasons = fixture.SpamProtectionRevokedReasons user.Id
        Assert.Contains("killed", reasons)

        let! snap = fixture.TryGetSnapshotUser user.Id
        Assert.True(snap.IsSome)
        Assert.False(snap.Value.spam_protection_until.HasValue, "snapshot_user.spam_protection_until should be cleared after revocation")
    }

    interface IAsyncDisposable with
        member _.DisposeAsync() = ValueTask(resetSpamProtectionSettings fixture :> Task)

    interface IClassFixture<MlAwaitFixture>

/// Test item 7: the grant-time notification is gated independently by SPAM_PROTECTION_NOTIFY_ENABLED.
type SpamProtectionNotifyTests(fixture: MlEnabledVahterTestContainers, _unused: MlAwaitFixture) =

    [<Fact>]
    let ``Notify ON: grant sends an ephemeral to the vetted user with the exact text`` () = task {
        do! fixture.SetBotSetting("SPAM_PROTECTION_ENABLED", "true")
        do! fixture.SetBotSetting("SPAM_PROTECTION_NOTIFY_ENABLED", "true")
        do! fixture.ReloadSettings()

        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222")
        let! _ = fixture.SendMessage msgUpdate
        let! deleted = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.True(deleted, "Sanity: message should have been auto-deleted as spam")
        let userId = msgUpdate.Message.Value.From.Value.Id

        do! fixture.ClearFakeCalls()
        let! callbackId = fixture.GetCallbackId msgUpdate.Message.Value "NotASpam"
        let! _ = fixture.SendMessage(Tg.callback(string callbackId, from = fixture.Vahters[0]))

        let! calls = fixture.GetFakeCalls "sendMessage"
        let notif =
            calls
            |> Array.tryFind (fun c ->
                c.Body.Contains $"\"chat_id\":{fixture.ChatsToMonitor[0].Id}"
                && c.Body.Contains $"\"receiver_user_id\":{userId}")
        Assert.True(notif.IsSome, "Grant with notify ON should send an ephemeral to the vetted user")

        let expectedText =
            "✅ Модератор проверил ваше удалённое сообщение — это была ошибка фильтра. "
            + "Приносим извинения, можете продолжать общение.\n\n"
            + "✅ A moderator reviewed your removed message — it was flagged in error. "
            + "Sorry about that, feel free to keep chatting."
        let actualText = JsonDocument.Parse(notif.Value.Body).RootElement.GetProperty("text").GetString()
        Assert.Equal(expectedText, actualText)
    }

    [<Fact>]
    let ``Notify OFF (default): grant sends no ephemeral to the vetted user`` () = task {
        do! fixture.SetBotSetting("SPAM_PROTECTION_ENABLED", "true")
        do! fixture.SetBotSetting("SPAM_PROTECTION_NOTIFY_ENABLED", "false")
        do! fixture.ReloadSettings()

        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222")
        let! _ = fixture.SendMessage msgUpdate
        let! deleted = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.True(deleted, "Sanity: message should have been auto-deleted as spam")
        let userId = msgUpdate.Message.Value.From.Value.Id

        do! fixture.ClearFakeCalls()
        let! callbackId = fixture.GetCallbackId msgUpdate.Message.Value "NotASpam"
        let! _ = fixture.SendMessage(Tg.callback(string callbackId, from = fixture.Vahters[0]))

        let! granted = fixture.SpamProtectionGranted userId
        Assert.True(granted, "Sanity: grant should still be recorded with notify off")

        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.False(
            calls |> Array.exists (fun c -> c.Body.Contains $"\"receiver_user_id\":{userId}"),
            "no ephemeral notification must be sent while SPAM_PROTECTION_NOTIFY_ENABLED is false")
    }

    interface IAsyncDisposable with
        member _.DisposeAsync() = ValueTask(resetSpamProtectionSettings fixture :> Task)

    interface IClassFixture<MlAwaitFixture>
