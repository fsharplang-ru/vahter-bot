module VahterBanBot.Tests.ReactionSpamTests

open System.Net
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

type ReactionSpamTests(fixture: MlEnabledVahterTestContainers) =

    [<Fact>]
    let ``User without messages gets banned after N reactions`` () = task {
        // User who never sent a message, just spams reactions
        let user = Tg.user()
        let chat = fixture.ChatsToMonitor[0]
        
        // Send 5 reactions (N = 5, threshold for ban)
        for i in 1..5 do
            let! resp =
                Tg.quickReaction(chat, i, user)
                |> fixture.SendMessage
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        
        // User should be banned by bot
        let! isBanned = fixture.UserBannedByBot user.Id
        Assert.True(isBanned, "User without messages should be banned after N reactions")
        
        // Reaction count should be recorded
        let! reactionCount = fixture.GetUserReactionCount user.Id
        Assert.Equal(5, reactionCount)
    }

    [<Fact>]
    let ``User with 1 message gets banned after N reactions`` () = task {
        // User who sent only 1 message (< M = 3), then spams reactions
        let user = Tg.user()
        let chat = fixture.ChatsToMonitor[0]
        
        // Send 1 message
        let! msgResp =
            Tg.quickMsg(chat = chat, from = user)
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, msgResp.StatusCode)
        
        // Send 5 reactions
        for i in 1..5 do
            let! resp =
                Tg.quickReaction(chat, 100 + i, user)
                |> fixture.SendMessage
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        
        // User should be banned (1 message < 3 required)
        let! isBanned = fixture.UserBannedByBot user.Id
        Assert.True(isBanned, "User with 1 message should be banned after N reactions")
    }

    [<Fact>]
    let ``User with 2 messages gets banned after N reactions`` () = task {
        // User who sent 2 messages (< M = 3), then spams reactions
        let user = Tg.user()
        let chat = fixture.ChatsToMonitor[0]
        
        // Send 2 messages
        for i in 1..2 do
            let! msgResp =
                Tg.quickMsg(chat = chat, from = user)
                |> fixture.SendMessage
            Assert.Equal(HttpStatusCode.OK, msgResp.StatusCode)
        
        // Send 5 reactions
        for i in 1..5 do
            let! resp =
                Tg.quickReaction(chat, 200 + i, user)
                |> fixture.SendMessage
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        
        // User should be banned (2 messages < 3 required)
        let! isBanned = fixture.UserBannedByBot user.Id
        Assert.True(isBanned, "User with 2 messages should be banned after N reactions")
    }

    [<Fact>]
    let ``User with M messages is NOT banned even with many reactions`` () = task {
        // User who sent M (3) messages should not be banned for reactions
        let user = Tg.user()
        let chat = fixture.ChatsToMonitor[0]
        
        // Send 3 messages (>= M)
        for i in 1..3 do
            let! msgResp =
                Tg.quickMsg(chat = chat, from = user)
                |> fixture.SendMessage
            Assert.Equal(HttpStatusCode.OK, msgResp.StatusCode)
        
        // Send 10 reactions (more than N)
        for i in 1..10 do
            let! resp =
                Tg.quickReaction(chat, 300 + i, user)
                |> fixture.SendMessage
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        
        // User should NOT be banned (has enough messages)
        let! isBanned = fixture.UserBannedByBot user.Id
        Assert.False(isBanned, "User with M+ messages should NOT be banned for reactions")
        
        // But reactions should still be counted
        let! reactionCount = fixture.GetUserReactionCount user.Id
        Assert.Equal(10, reactionCount)
    }

    [<Fact>]
    let ``Reaction-spam auto-ban does NOT wipe Detected Spam cards`` () = task {
        // Reaction-spam ban is always automatic (Actor.Bot). With the fix, TotalBanByReaction
        // no longer sweeps existing callback messages — they remain as audit trail and age
        // out via DetectedSpamCleanupAge in Cleanup.runCleanup.
        let user = Tg.user()
        let chat = fixture.ChatsToMonitor[0]

        // 1 ML-detected spam → DeleteSpam posts a Detected Spam card with NotASpam button
        let spam = Tg.quickMsg(chat = chat, text = "2222222", from = user)
        let! _ = fixture.SendMessage spam

        let! callbackId = fixture.GetCallbackId spam.Message "NotASpam"

        do! fixture.ClearFakeCalls()

        // 5 reactions → TotalBanByReaction(Actor.Bot)
        for i in 1..5 do
            let! resp =
                Tg.quickReaction(chat, 600 + i, user)
                |> fixture.SendMessage
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let! isBanned = fixture.UserBannedByBot user.Id
        Assert.True(isBanned, "User should be banned after reaction spam")

        // The Detected Spam card must not have been deleted from the channel
        let! delCalls = fixture.GetFakeCalls("deleteMessage")
        let detectedSpamId = fixture.DetectedSpamChannel.Id
        let detectedSpamDeletes =
            delCalls |> Array.filter (fun c -> c.Body.Contains $"\"chat_id\":{detectedSpamId}")
        Assert.Equal(0, detectedSpamDeletes.Length)

        // The callback must still be active (no CallbackExpired event)
        let! expired = fixture.HasCallbackExpired callbackId
        Assert.False(expired, "Callback should remain active after reaction-spam auto-ban")
    }

    [<Fact>]
    let ``Reactions in non-monitored chat are ignored`` () = task {
        // Reactions in a random chat should not trigger ban
        let user = Tg.user()
        let randomChat = Tg.chat() // Not in ChatsToMonitor
        
        // Send 10 reactions in random chat
        for i in 1..10 do
            let! resp =
                Tg.quickReaction(randomChat, 400 + i, user)
                |> fixture.SendMessage
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        
        // User should NOT be banned
        let! isBanned = fixture.UserBannedByBot user.Id
        Assert.False(isBanned, "Reactions in non-monitored chat should not trigger ban")
        
        // Reactions should NOT be counted
        let! reactionCount = fixture.GetUserReactionCount user.Id
        Assert.Equal(0, reactionCount)
    }


type ReactionSpamDisabledTests(fixture: MlDisabledVahterTestContainers) =

    [<Fact>]
    let ``Reactions are ignored when feature is disabled`` () = task {
        // When REACTION_SPAM_ENABLED=false, reactions should be ignored
        let user = Tg.user()
        let chat = fixture.ChatsToMonitor[0]
        
        // Send 10 reactions
        for i in 1..10 do
            let! resp =
                Tg.quickReaction(chat, 500 + i, user)
                |> fixture.SendMessage
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        
        // User should NOT be banned (feature disabled)
        let! isBanned = fixture.UserBannedByBot user.Id
        Assert.False(isBanned, "Reactions should be ignored when feature is disabled")
        
        // Reactions should NOT be counted (feature disabled)
        let! reactionCount = fixture.GetUserReactionCount user.Id
        Assert.Equal(0, reactionCount)
    }

