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

        // Audit message must be posted to AllLogs naming the vahter who decided
        let! sendCalls = fixture.GetFakeCalls("sendMessage")
        let allLogsId = fixture.AllLogsChannel.Id
        let banAuditLogs =
            sendCalls
            |> Array.filter (fun c ->
                c.Body.Contains $"\"chat_id\":{allLogsId}"
                && c.Body.Contains "Reaction-triage BAN"
                && c.Body.Contains $"@{vahter.Username}")
        Assert.True(banAuditLogs.Length >= 1, "AllLogs should record the BAN action with the vahter's name")
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

        // Audit message must be posted to AllLogs naming the vahter and the originating chat
        let! sendCalls = fixture.GetFakeCalls("sendMessage")
        let allLogsId = fixture.AllLogsChannel.Id
        let originatingChat = fixture.ChatsToMonitor[0]
        let spamAuditLogs =
            sendCalls
            |> Array.filter (fun c ->
                c.Body.Contains $"\"chat_id\":{allLogsId}"
                && c.Body.Contains "Reaction-triage SPAM"
                && c.Body.Contains $"@{vahter.Username}"
                && c.Body.Contains $"@{originatingChat.Username}")
        Assert.True(spamAuditLogs.Length >= 1, "AllLogs should record the SPAM action with the vahter's name and originating chat")
    }

    [<Fact>]
    let ``Vahter clicks NOT SPAM → cooldown set with Actor.User, no ban, audit logged`` () = task {
        let vahter = fixture.Vahters[0]
        do! seedVahterInDb fixture vahter

        let user = Tg.user()
        do! tripThreshold fixture user 3000

        do! fixture.ClearFakeCalls()

        let! notSpamId = fixture.GetReactionCallbackId(user.Id, "ReactionNotSpam")
        let! resp = fixture.ClickCallback(notSpamId, vahter)
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let! cooldownSet = fixture.HasReactionCooldown user.Id
        Assert.True(cooldownSet, "NOT SPAM button should set the cooldown")

        // Cooldown actor must be the vahter, not the bot — analytics rely on this attribution.
        let! cooldownActor = fixture.TryGetReactionCooldownActorCase user.Id
        Assert.Equal(Some "User", cooldownActor)

        // Audit message must be posted to AllLogs naming the vahter
        let! sendCalls = fixture.GetFakeCalls("sendMessage")
        let allLogsId = fixture.AllLogsChannel.Id
        let notSpamAuditLogs =
            sendCalls
            |> Array.filter (fun c ->
                c.Body.Contains $"\"chat_id\":{allLogsId}"
                && c.Body.Contains "Reaction-triage NOT SPAM"
                && c.Body.Contains $"@{vahter.Username}")
        Assert.True(notSpamAuditLogs.Length >= 1, "AllLogs should record the NOT SPAM action with the vahter's name")

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
    let ``Admin alert posted to Potential Spam channel (interactive) and mirrored to All Logs (audit)`` () = task {
        // Routing matters: PotentialSpamChannel is the zero-inbox actionable channel where
        // vahters click buttons; AllLogsChannel is read-only persistent audit. Both must see
        // the alert, but only PotentialSpamChannel gets the interactive copy.
        let user = Tg.user()
        do! fixture.ClearFakeCalls()
        do! tripThreshold fixture user 13000

        // The interactive alert is sent as sendMessage (no profile photo on fake-tg) to
        // PotentialSpamChannel.
        let! sendCalls = fixture.GetFakeCalls("sendMessage")
        let potentialSpamId = fixture.PotentialSpamChannel.Id
        let allLogsId = fixture.AllLogsChannel.Id

        let interactiveAlerts =
            sendCalls
            |> Array.filter (fun c ->
                c.Body.Contains $"\"chat_id\":{potentialSpamId}"
                && c.Body.Contains "Reaction-spam triage")
        Assert.True(interactiveAlerts.Length >= 1, "Expected interactive alert in PotentialSpamChannel")
        Assert.True(interactiveAlerts[0].Body.Contains "inline_keyboard", "PotentialSpamChannel alert must carry the 3 callback buttons")

        let auditAlerts =
            sendCalls
            |> Array.filter (fun c ->
                c.Body.Contains $"\"chat_id\":{allLogsId}"
                && c.Body.Contains "Reaction-spam triage")
        Assert.True(auditAlerts.Length >= 1, "Expected audit mirror in AllLogsChannel")
        Assert.False(auditAlerts[0].Body.Contains "inline_keyboard", "AllLogsChannel mirror must NOT carry buttons (read-only audit)")

        // The LLM annotation line must reference the verdict the LLM returned — that's
        // the load-bearing purpose of shadow mode.
        Assert.True(
            interactiveAlerts[0].Body.Contains "LLM (shadow) said:" || interactiveAlerts[0].Body.Contains "UNSURE",
            "Alert should annotate the shadow LLM verdict so vahter can compare")
    }

    [<Fact>]
    let ``Suspect with username renders as handle mention; suspect without username renders as tg-user link`` () = task {
        // The vahter alert needs to surface a clickable profile so the vahter can read the bio
        // themselves — bots can't see bios for privacy-strict users, humans can. Two paths:
        //   (a) Username present → "@handle (user_id)"  (Telegram auto-links the @handle;
        //       numeric id in parens is for vahters who grep logs / event streams).
        //   (b) Username missing → "<a href='tg://user?id=…'>Display Name</a> (user_id)"  (only
        //       way to make a no-handle account clickable; id still in parens for copy-paste).
        let userWithHandle = Tg.user(username = $"svetla_{System.Guid.NewGuid().ToString().[..7]}")
        do! fixture.ClearFakeCalls()
        do! tripThreshold fixture userWithHandle 14000

        let! sendCalls = fixture.GetFakeCalls("sendMessage")
        let potentialSpamId = fixture.PotentialSpamChannel.Id
        let alertsForWithHandle =
            sendCalls
            |> Array.filter (fun c ->
                c.Body.Contains $"\"chat_id\":{potentialSpamId}"
                && c.Body.Contains "Reaction-spam triage"
                && c.Body.Contains $"@{userWithHandle.Username}")
        Assert.True(alertsForWithHandle.Length >= 1, "Expected interactive alert for user with handle")
        let withHandleBody = alertsForWithHandle[0].Body
        // Telegram.Bot 22.x serializes ParseMode via .NET enum name → "Html"; Telegram
        // accepts case-insensitively. Tolerate either casing so the test isn't tied to
        // the SDK's enum serialization style.
        Assert.True(
            withHandleBody.Contains "\"parse_mode\":\"HTML\""
            || withHandleBody.Contains "\"parse_mode\":\"Html\"",
            "Alert must be sent with parse_mode=HTML")
        Assert.Contains($"@{userWithHandle.Username} ({userWithHandle.Id})", withHandleBody)
        Assert.DoesNotContain($"tg://user?id={userWithHandle.Id}", withHandleBody)

        // Now the no-username path: must fall back to an HTML profile link
        let userNoHandle = Tg.user()  // Tg.user() defaults username to null
        Assert.Null(userNoHandle.Username)
        do! fixture.ClearFakeCalls()
        do! tripThreshold fixture userNoHandle 15000

        let! sendCalls2 = fixture.GetFakeCalls("sendMessage")
        let alertsForNoHandle =
            sendCalls2
            |> Array.filter (fun c ->
                c.Body.Contains $"\"chat_id\":{potentialSpamId}"
                && c.Body.Contains "Reaction-spam triage"
                && c.Body.Contains (string userNoHandle.Id))
        Assert.True(alertsForNoHandle.Length >= 1, "Expected interactive alert for user without handle")
        let noHandleBody = alertsForNoHandle[0].Body
        Assert.Contains($"tg://user?id={userNoHandle.Id}", noHandleBody)
        // No-handle case also gets the id in parens after the link text
        Assert.Contains($"({userNoHandle.Id})", noHandleBody)
    }

    [<Fact>]
    let ``Reaction emoji is recorded and shown in the dossier "reacted X" line`` () = task {
        // Vahter request: it's useful to see WHICH emoji the user reacted with — repeating
        // 🔥 on random messages is a stronger spam signal than alternating ❤️/👍 etc.
        let user = Tg.user()
        let chat = fixture.ChatsToMonitor[0]

        do! fixture.ClearFakeCalls()

        // Use a non-default emoji so the alert clearly shows it (default in Tg.quickReaction is 👍)
        let fireEmoji = "\U0001F525"  // 🔥
        for i in 1..5 do
            let! resp =
                Tg.quickReaction(chat, 17000 + i, user, emoji = fireEmoji)
                |> fixture.SendMessage
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let! sendCalls = fixture.GetFakeCalls("sendMessage")
        let potentialSpamId = fixture.PotentialSpamChannel.Id
        let interactive =
            sendCalls
            |> Array.filter (fun c -> c.Body.Contains $"\"chat_id\":{potentialSpamId}" && c.Body.Contains "Reaction-spam triage")
        Assert.True(interactive.Length >= 1, "Expected an interactive alert in PotentialSpamChannel")

        let body = interactive[0].Body
        // The alert dossier must contain at least one "reacted 🔥" line; 🔥 is the
        // JSON-escaped surrogate pair for 🔥.
        Assert.True(
            body.Contains "reacted \\uD83D\\uDD25" || body.Contains "reacted 🔥",
            $"Dossier should show the actual emoji used; body did not contain 'reacted 🔥'")
    }

    [<Fact>]
    let ``Pre-PR reactions (no chatId recorded) render as (unknown), not as fake chat 0`` () = task {
        // Backward-compat: events recorded before this PR have no chatId/messageId/emoji.
        // We must not invent a "chat 0" label that pretends to be a real chat.
        let user = Tg.user()

        // Synthesize an old-shape reaction event directly in the DB (bypassing the bot).
        use conn = new Npgsql.NpgsqlConnection(fixture.DbConnectionString)
        let insertSql =
            "INSERT INTO event(stream_id, stream_version, data) VALUES " +
            "('user:' || @userId, " +
            " (SELECT COALESCE(MAX(stream_version), 0) + 1 FROM event WHERE stream_id = 'user:' || @userId), " +
            " jsonb_build_object('Case','UserReactionRecorded','userId',@userId,'delta',1))"
        let! _ = Dapper.SqlMapper.ExecuteAsync(conn, insertSql, {| userId = user.Id |})

        // Now trip the threshold so the alert is built with that old event present in the dossier.
        do! fixture.ClearFakeCalls()
        do! tripThreshold fixture user 19000

        let! sendCalls = fixture.GetFakeCalls("sendMessage")
        let potentialSpamId = fixture.PotentialSpamChannel.Id
        let interactive =
            sendCalls
            |> Array.filter (fun c -> c.Body.Contains $"\"chat_id\":{potentialSpamId}" && c.Body.Contains "Reaction-spam triage")
        Assert.True(interactive.Length >= 1)
        let body = interactive[0].Body

        // The dossier must NOT contain a bullet line with "0  reacted" — chat id 0 is not a real chat.
        Assert.DoesNotContain("  0  reacted", body)
        // It SHOULD contain an honest "(unknown)" marker (HTML-escaped <i>)
        Assert.Contains("(unknown)", body)
    }

    [<Fact>]
    let ``Originating chat is shown as chat username with id in parens`` () = task {
        // Bare "[chat -1001685850502]" is useless — vahters want to see which monitored chat
        // tripped the threshold. But the numeric id is still needed for SQL/event-stream
        // lookups, so render both: "@chatname (chat_id)".
        let user = Tg.user()
        do! fixture.ClearFakeCalls()
        do! tripThreshold fixture user 16000

        let! sendCalls = fixture.GetFakeCalls("sendMessage")
        let potentialSpamId = fixture.PotentialSpamChannel.Id
        let interactive =
            sendCalls
            |> Array.filter (fun c -> c.Body.Contains $"\"chat_id\":{potentialSpamId}" && c.Body.Contains "Reaction-spam triage")
        Assert.True(interactive.Length >= 1)
        let body = interactive[0].Body

        let originatingChat = fixture.ChatsToMonitor[0]   // tripThreshold uses ChatsToMonitor[0]
        Assert.Contains($"@{originatingChat.Username} ({originatingChat.Id})", body)
        // The literal numeric-only "[chat -666]" form must NOT appear (we use @name (id) instead)
        Assert.DoesNotContain($"[chat {originatingChat.Id}]", body)
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
