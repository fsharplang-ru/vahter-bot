module VahterBanBot.Tests.ReactionSpamTests

open System.Net
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

type TgUser = Telegram.Bot.Types.User

/// Reaction-spam triage tests. After PR #?? the threshold no longer auto-bans —
/// it builds a dossier, runs a vision LLM in shadow mode (records verdict but defers to
/// vahter), and posts an admin alert with [BAN] [SPAM] [NOT SPAM] buttons. Tests live
/// on the ML-enabled fixture which has both REACTION_SPAM_ENABLED=true and the LLM
/// endpoint pointed at fakeAzureOpenAi.
///
/// The fake LLM routes verdict by keywords in the request body:
///   "ban-me"      → BAN
///   "react-spam"  → SPAM
///   "real-lurker" → NOT_SPAM
///   otherwise     → UNSURE
type ReactionSpamTriageTests(fixture: MlEnabledVahterTestContainers) =

    /// Helper: trip the threshold by sending REACTION_SPAM_MAX_REACTIONS reactions from a
    /// user with fewer messages than REACTION_SPAM_MIN_MESSAGES. Returns nothing —
    /// callers assert on DB state.
    let tripThreshold (fixture: MlEnabledVahterTestContainers) (user: TgUser) (msgIdBase: int) = task {
        let chat = fixture.ChatsToMonitor[0]
        for i in 1..5 do
            let! resp =
                Tg.quickReaction(chat, msgIdBase + i, user)
                |> fixture.SendMessage
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
    }

    /// OnCallback bails with "you are not in DB" if the clicker has no event-store record yet.
    /// Tests for vahter button clicks need to seed the vahter first — easiest path is to send
    /// a normal message from them, which records UsernameChanged on user:{vahterId}.
    let seedVahterInDb (fixture: MlEnabledVahterTestContainers) (vahter: TgUser) = task {
        let chat = fixture.ChatsToMonitor[0]
        let! resp = Tg.quickMsg(chat = chat, from = vahter) |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
    }

    [<Fact>]
    let ``Shadow mode: threshold trip records LLM verdict + posts admin alert without auto-banning`` () = task {
        // Default fixture has LLM_REACTION_TRIAGE_AUTO_ACT=false → shadow mode.
        // Default user firstName is a Guid (no keyword) → fake LLM returns UNSURE.
        let user = Tg.user()
        do! tripThreshold fixture user 1000

        // LLM was called and verdict was recorded
        let! verdict = fixture.TryGetReactionTriageVerdict user.Id
        Assert.Equal(Some "UNSURE", verdict)

        // Shadow-mode flag is recorded on the event
        let! shadowMode = fixture.TryGetReactionTriageShadowMode user.Id
        Assert.Equal(Some true, shadowMode)

        // Reason field is captured
        let! reason = fixture.TryGetReactionTriageReason user.Id
        Assert.True(reason.IsSome && reason.Value.Length > 0, "Reason should be populated")

        // No autonomous action — user is NOT banned
        let! isBanned = fixture.UserBannedByBot user.Id
        Assert.False(isBanned, "Shadow mode must not auto-ban; vahter clicks the button.")

        // The 3 reaction-triage callbacks are present (BAN / SPAM / NOT SPAM buttons)
        let! _ = fixture.GetReactionCallbackId(user.Id, "ReactionBan")
        let! _ = fixture.GetReactionCallbackId(user.Id, "ReactionSpam")
        let! _ = fixture.GetReactionCallbackId(user.Id, "ReactionNotSpam")
        ()
    }

    [<Fact>]
    let ``Vahter clicks BAN → user banned with Actor.User (not Bot), DeleteMessageReaction called`` () = task {
        // Regression lock: ReactionAct_Ban used to hardcode Actor.Bot, so a vahter click was
        // attributed to the bot. The dedup also used to collide on moderation:{chatId}:0
        // across users, so this whole flow silently no-op'd on the second test of the run.
        let vahter = fixture.Vahters[0]
        do! seedVahterInDb fixture vahter

        let user = Tg.user()
        do! tripThreshold fixture user 2000

        do! fixture.ClearFakeCalls()

        let! banId = fixture.GetReactionCallbackId(user.Id, "ReactionBan")
        let! resp = fixture.ClickCallback(banId, vahter)
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        // Actor on the UserBanned event must be the vahter, not the bot
        let! bannedByVahter = fixture.UserBanned user.Id
        Assert.True(bannedByVahter, "BAN must record Actor.User (vahter), so UserBanned (vahter-case query) returns true")

        let! bannedByBot = fixture.UserBannedByBot user.Id
        Assert.False(bannedByBot, "Vahter-initiated BAN must NOT be recorded as Actor.Bot")

        // The 5 reactions recorded by tripThreshold must each get a DeleteMessageReaction call
        // (chat_id + message_id + user_id targeting the suspect)
        let! drCalls = fixture.GetFakeCalls("deleteMessageReaction")
        let targeted =
            drCalls
            |> Array.filter (fun c ->
                c.Body.Contains $"\"user_id\":{user.Id}"
                && c.Body.Contains $"\"chat_id\":{fixture.ChatsToMonitor[0].Id}")
        Assert.True(targeted.Length >= 5, $"Expected ≥5 DeleteMessageReaction calls for user {user.Id}, got {targeted.Length}")

        // No cooldown should be set for a ban
        let! cooldown = fixture.HasReactionCooldown user.Id
        Assert.False(cooldown, "BAN must not set a cooldown — the user is banned")
    }

    [<Fact>]
    let ``Vahter clicks SPAM → reactions removed in originating chat + can_react_to_messages restricted, no ban`` () = task {
        // SPAM verdict happy path was uncovered. Validates: (a) RestrictChatMember called with
        // can_react_to_messages=false, (b) DeleteMessageReaction called only for the originating
        // chat, (c) no UserBanned event, (d) no cooldown event.
        let vahter = fixture.Vahters[0]
        do! seedVahterInDb fixture vahter

        let user = Tg.user()
        do! tripThreshold fixture user 2500

        do! fixture.ClearFakeCalls()

        let! spamId = fixture.GetReactionCallbackId(user.Id, "ReactionSpam")
        let! resp = fixture.ClickCallback(spamId, vahter)
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        // restrictChatMember called with can_react_to_messages=false targeting the suspect
        let! restrictCalls = fixture.GetFakeCalls("restrictChatMember")
        let restrictForUser =
            restrictCalls
            |> Array.filter (fun c ->
                c.Body.Contains $"\"user_id\":{user.Id}"
                && c.Body.Contains "\"can_react_to_messages\":false")
        Assert.True(restrictForUser.Length >= 1, $"Expected at least one restrictChatMember(can_react_to_messages=false) for user {user.Id}, got {restrictForUser.Length}")

        // DeleteMessageReaction targets only the originating chat
        let originatingChatId = fixture.ChatsToMonitor[0].Id
        let! drCalls = fixture.GetFakeCalls("deleteMessageReaction")
        let inOriginating =
            drCalls
            |> Array.filter (fun c ->
                c.Body.Contains $"\"user_id\":{user.Id}"
                && c.Body.Contains $"\"chat_id\":{originatingChatId}")
        Assert.True(inOriginating.Length >= 5, $"Expected ≥5 DeleteMessageReaction for user {user.Id} in chat {originatingChatId}, got {inOriginating.Length}")

        // Must NOT be banned and must NOT have a cooldown
        let! isBanned = fixture.UserBanned user.Id
        Assert.False(isBanned, "SPAM must not ban")
        let! bannedByBot = fixture.UserBannedByBot user.Id
        Assert.False(bannedByBot, "SPAM must not ban (bot case)")
        let! cooldown = fixture.HasReactionCooldown user.Id
        Assert.False(cooldown, "SPAM must not set a NOT_SPAM cooldown — only NOT_SPAM does")
    }

    [<Fact>]
    let ``Vahter clicks NOT SPAM → cooldown set with Actor.User, no ban`` () = task {
        let vahter = fixture.Vahters[0]
        do! seedVahterInDb fixture vahter

        let user = Tg.user()
        do! tripThreshold fixture user 3000

        let! notSpamId = fixture.GetReactionCallbackId(user.Id, "ReactionNotSpam")
        let! resp = fixture.ClickCallback(notSpamId, vahter)
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let! cooldownSet = fixture.HasReactionCooldown user.Id
        Assert.True(cooldownSet, "NOT SPAM button should set the cooldown")

        // Cooldown actor must be the vahter, not the bot — analytics rely on this attribution.
        let! cooldownActor = fixture.TryGetReactionCooldownActorCase user.Id
        Assert.Equal(Some "User", cooldownActor)

        let! isBanned = fixture.UserBanned user.Id
        Assert.False(isBanned, "NOT SPAM must not ban")
    }

    [<Fact>]
    let ``Two users tripping in the same chat get independent callbacks and independent ban actions`` () = task {
        // Regression lock: the dedup used to key on moderation:{chatId}:messageId=0, so the
        // SECOND user tripping in the same chat would silently no-op on click (the helper saw
        // the first user's record and returned actionRecorded=false). This test would have
        // failed on `bannedB` before that fix.
        let vahter = fixture.Vahters[0]
        do! seedVahterInDb fixture vahter

        let userA = Tg.user()
        let userB = Tg.user()
        do! tripThreshold fixture userA 11000
        do! tripThreshold fixture userB 12000

        // Each user got their own 3 callbacks
        let! banA = fixture.GetReactionCallbackId(userA.Id, "ReactionBan")
        let! banB = fixture.GetReactionCallbackId(userB.Id, "ReactionBan")
        Assert.NotEqual(banA, banB)

        // Click BAN on A, then BAN on B — both must take effect independently
        let! _ = fixture.ClickCallback(banA, vahter)
        let! _ = fixture.ClickCallback(banB, vahter)

        let! aBanned = fixture.UserBanned userA.Id
        Assert.True(aBanned, "User A should be banned after vahter clicked BAN(A)")
        let! bBanned = fixture.UserBanned userB.Id
        Assert.True(bBanned, "User B should also be banned — second click in same chat must not be deduped against first")
    }

    [<Fact>]
    let ``Admin alert annotates the LLM verdict shown to vahters (shadow mode)`` () = task {
        // Shadow value of the LLM must be visible to humans — that's the entire point of the
        // shadow mode rollout: vahters sanity-check the model before we promote it to autonomous.
        let user = Tg.user()
        do! fixture.ClearFakeCalls()
        do! tripThreshold fixture user 13000

        // Alert is sent as a photo OR a plain message to AllLogsChannel. The fake getUserProfilePhotos
        // returns no photos, so the alert is a sendMessage to the logs channel containing the dossier.
        let! sendCalls = fixture.GetFakeCalls("sendMessage")
        let allLogsId = fixture.AllLogsChannel.Id
        let alertCalls =
            sendCalls
            |> Array.filter (fun c ->
                c.Body.Contains $"\"chat_id\":{allLogsId}"
                && c.Body.Contains "Reaction-spam triage")
        Assert.True(alertCalls.Length >= 1, "Expected at least one reaction-triage admin alert in AllLogsChannel")

        // The LLM annotation line must reference the verdict the LLM returned
        let annotatedAlerts =
            alertCalls
            |> Array.filter (fun c -> c.Body.Contains "LLM (shadow) said:" || c.Body.Contains "UNSURE")
        Assert.True(annotatedAlerts.Length >= 1, "Alert should annotate the shadow LLM verdict so vahter can compare")
    }

    [<Fact>]
    let ``Cooldown short-circuits: subsequent reactions don't re-trigger triage`` () = task {
        let vahter = fixture.Vahters[0]
        do! seedVahterInDb fixture vahter

        let user = Tg.user()
        do! tripThreshold fixture user 4000

        // First trip → NOT_SPAM click → cooldown event
        let! notSpamId = fixture.GetReactionCallbackId(user.Id, "ReactionNotSpam")
        let! _ = fixture.ClickCallback(notSpamId, vahter)
        let! cooldownSet = fixture.HasReactionCooldown user.Id
        Assert.True(cooldownSet)

        // Now react again — this should NOT trigger a new triage event because of cooldown
        let chat = fixture.ChatsToMonitor[0]
        for i in 1..5 do
            let! resp =
                Tg.quickReaction(chat, 4100 + i, user)
                |> fixture.SendMessage
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        // Verdict count should stay at 1 (the original trip), not increase
        use conn = new Npgsql.NpgsqlConnection(fixture.DbConnectionString)
        let! count =
            Dapper.SqlMapper.QuerySingleAsync<int>(
                conn,
                "SELECT COUNT(*)::INT FROM event WHERE event_type = 'LlmReactionTriageClassified' AND (data->>'userId')::BIGINT = @userId",
                {| userId = user.Id |})
        Assert.Equal(1, count)
    }

    [<Fact>]
    let ``Reactions in non-monitored chat are ignored`` () = task {
        let user = Tg.user()
        let randomChat = Tg.chat()  // Not in ChatsToMonitor

        for i in 1..10 do
            let! resp =
                Tg.quickReaction(randomChat, 5000 + i, user)
                |> fixture.SendMessage
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let! isBanned = fixture.UserBannedByBot user.Id
        Assert.False(isBanned, "Reactions in non-monitored chat should not trigger triage")

        let! verdict = fixture.TryGetReactionTriageVerdict user.Id
        Assert.Equal(None, verdict)

        let! reactionCount = fixture.GetUserReactionCount user.Id
        Assert.Equal(0, reactionCount)
    }


// NOTE: autonomous-mode tests (LLM_REACTION_TRIAGE_AUTO_ACT=true) are deliberately not added
// here yet. Per the plan's rollout order, the feature ships in shadow mode first; the
// autonomous flag flip is a config change with the same code path (the goAutonomous branch in
// RunReactionTriagePipeline). Once we promote the LLM in prod we'll add coverage for autoAct
// behavior — until then, flipping the flag inside a shared MlEnabledVahterTestContainers
// fixture would race with neighbouring LLM-triage tests.


type ReactionSpamDisabledTests(fixture: MlDisabledVahterTestContainers) =

    [<Fact>]
    let ``Reactions are ignored when feature is disabled`` () = task {
        // When REACTION_SPAM_ENABLED=false, reactions should be ignored entirely
        let user = Tg.user()
        let chat = fixture.ChatsToMonitor[0]

        for i in 1..10 do
            let! resp =
                Tg.quickReaction(chat, 9000 + i, user)
                |> fixture.SendMessage
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let! isBanned = fixture.UserBannedByBot user.Id
        Assert.False(isBanned, "Reactions should be ignored when feature is disabled")

        let! reactionCount = fixture.GetUserReactionCount user.Id
        Assert.Equal(0, reactionCount)

        // No triage verdict either
        let! verdict = fixture.TryGetReactionTriageVerdict user.Id
        Assert.Equal(None, verdict)
    }
