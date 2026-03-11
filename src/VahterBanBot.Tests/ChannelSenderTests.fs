module VahterBanBot.Tests.ChannelSenderTests

open System.Net
open VahterBanBot.Tests.ContainerTestBase
open VahterBanBot.Tests.TgMessageUtils
open Xunit

type ChannelSenderTests(fixture: MlEnabledVahterTestContainers, _unused: MlAwaitFixture) =

    // ──── Group 1: Sender Resolution & Message Storage ────

    [<Fact>]
    let ``Unknown channel message stored with channel ID`` () = task {
        let spamChannel = Tg.channel(username = "unknown_store_ch", title = "Unknown Store Channel")
        let msgUpdate = Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = "aaaaaaa",
            from = Tg.channelBot(),
            senderChat = spamChannel
        )
        let! resp = fixture.SendMessage msgUpdate
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let! dbMsg = fixture.TryGetDbMessage msgUpdate.Message
        Assert.True(dbMsg.IsSome, "Message should be stored in DB")
        Assert.Equal(spamChannel.Id, dbMsg.Value.user_id)
    }

    [<Fact>]
    let ``Linked channel message stored with Telegram system user ID`` () = task {
        let linkedChannel = Tg.channel(username = "linked_store_ch", title = "Linked Store Channel")
        let msgUpdate = Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = "aaaaaaa",
            from = Tg.telegramUser(),
            senderChat = linkedChannel,
            isAutomaticForward = true
        )
        let! resp = fixture.SendMessage msgUpdate
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let! dbMsg = fixture.TryGetDbMessage msgUpdate.Message
        Assert.True(dbMsg.IsSome, "Message should be stored in DB")
        // Linked channels use telegramUser (not Channel_Bot), so IsChannelSender = false
        // They keep the From.Id (777000)
        Assert.Equal(777000L, dbMsg.Value.user_id)
    }

    // ──── Group 2: Trusted Channel — ML Skip ────

    [<Fact>]
    let ``Linked channel sending spam text is NOT auto-deleted`` () = task {
        let linkedChannel = Tg.channel(username = "linked_spam_ch", title = "Linked Spam Channel")
        let msgUpdate = Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = "2222222",
            from = Tg.telegramUser(),
            senderChat = linkedChannel,
            isAutomaticForward = true
        )
        let! _ = fixture.SendMessage msgUpdate

        let! autoDeleted = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.False(autoDeleted, "Linked channel message should NOT be auto-deleted even with spam text")

        let! dbMsg = fixture.TryGetDbMessage msgUpdate.Message
        Assert.True(dbMsg.IsSome, "Message should be stored in DB")
    }

    [<Fact>]
    let ``Monitored channel sending spam text is NOT auto-deleted`` () = task {
        // Create a channel with the same ID as a monitored chat
        let monitoredAsChannel = Tg.channel(id = fixture.ChatsToMonitor[0].Id, username = "pro.hell", title = "Monitored Channel")
        let msgUpdate = Tg.quickMsg(
            chat = fixture.ChatsToMonitor[1],
            text = "7777777",
            from = Tg.channelBot(),
            senderChat = monitoredAsChannel
        )
        let! _ = fixture.SendMessage msgUpdate

        let! autoDeleted = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.False(autoDeleted, "Monitored channel message should NOT be auto-deleted even with spam text")
    }

    [<Fact>]
    let ``Unknown channel sending spam text IS auto-deleted`` () = task {
        let unknownChannel = Tg.channel(username = "spam_ch_1", title = "Spam Channel")
        let msgUpdate = Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = "2222222",
            from = Tg.channelBot(),
            senderChat = unknownChannel
        )
        let! _ = fixture.SendMessage msgUpdate

        let! autoDeleted = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.True(autoDeleted, "Unknown channel spam should be auto-deleted")

        let! dbMsg = fixture.TryGetDbMessage msgUpdate.Message
        Assert.True(dbMsg.IsSome, "Message should be stored in DB")
        Assert.Equal(unknownChannel.Id, dbMsg.Value.user_id)
    }

    // ──── Group 3: Unknown Channel — Spam & Ham ────

    [<Fact>]
    let ``Unknown channel spam is auto-deleted and stored with channel ID`` () = task {
        let spamChannel = Tg.channel(username = "spam_ch_2", title = "Spam Channel 2")
        let msgUpdate = Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = "7777777",
            from = Tg.channelBot(),
            senderChat = spamChannel
        )
        let! _ = fixture.SendMessage msgUpdate

        let! autoDeleted = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.True(autoDeleted, "Unknown channel spam should be auto-deleted")

        let! dbMsg = fixture.TryGetDbMessage msgUpdate.Message
        Assert.True(dbMsg.IsSome, "Message should be stored in DB")
        Assert.Equal(spamChannel.Id, dbMsg.Value.user_id)
    }

    [<Fact>]
    let ``Unknown channel ham is NOT deleted and stored with channel ID`` () = task {
        let hamChannel = Tg.channel(username = "ham_ch_1", title = "Ham Channel")
        let msgUpdate = Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = "aaaaaaa",
            from = Tg.channelBot(),
            senderChat = hamChannel
        )
        let! _ = fixture.SendMessage msgUpdate

        let! autoDeleted = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.False(autoDeleted, "Unknown channel ham should NOT be auto-deleted")

        let! dbMsg = fixture.TryGetDbMessage msgUpdate.Message
        Assert.True(dbMsg.IsSome, "Message should be stored in DB")
        Assert.Equal(hamChannel.Id, dbMsg.Value.user_id)
    }

    [<Fact>]
    let ``Vahter ban on channel spam targets channel ID`` () = task {
        let spamChannel = Tg.channel(username = "ban_target_ch", title = "Ban Target Channel")
        let msgUpdate = Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = "spam text here",
            from = Tg.channelBot(),
            senderChat = spamChannel
        )
        let! _ = fixture.SendMessage msgUpdate

        // Vahter bans by replying
        let! banResp =
            Tg.replyMsg(msgUpdate.Message, "/ban", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)

        // Assert ban targets the channel ID, not Channel_Bot
        let! msgBanned = fixture.MessageBanned msgUpdate.Message
        Assert.True(msgBanned, "Channel spam message should be banned")

        let! channelBanned = fixture.UserBanned spamChannel.Id
        Assert.True(channelBanned, "The spam channel should be banned (by channel ID)")

        let! channelBotBanned = fixture.UserBanned 136817688L
        Assert.False(channelBotBanned, "Channel_Bot itself should NOT be banned")
    }

    // ──── Group 4: Ban Isolation — Critical Safety ────

    [<Fact>]
    let ``Ban one channel does not affect another channel's messages`` () = task {
        let channelA = Tg.channel(username = "ch_a_iso", title = "Channel A")
        let channelB = Tg.channel(username = "ch_b_iso", title = "Channel B")

        // Send message from Channel A
        let msgA = Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = "spam from A",
            from = Tg.channelBot(),
            senderChat = channelA
        )
        let! _ = fixture.SendMessage msgA

        // Send message from Channel B
        let msgB = Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = "hello from B",
            from = Tg.channelBot(),
            senderChat = channelB
        )
        let! _ = fixture.SendMessage msgB

        // Ban Channel A
        let! banResp =
            Tg.replyMsg(msgA.Message, "/ban", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)

        // Channel A's message is banned
        let! msgABanned = fixture.MessageBanned msgA.Message
        Assert.True(msgABanned, "Channel A's message should be banned")

        // Channel B's message is still in DB (not affected)
        let! dbMsgB = fixture.TryGetDbMessage msgB.Message
        Assert.True(dbMsgB.IsSome, "Channel B's message should still exist in DB")

        // Channel B is not banned
        let! channelBBanned = fixture.UserBanned channelB.Id
        Assert.False(channelBBanned, "Channel B should NOT be banned")
    }

    [<Fact>]
    let ``Ban channel does not affect regular user's messages`` () = task {
        let regularUser = Tg.user()
        let spamChannel = Tg.channel(username = "ch_iso_user", title = "Spam Channel Iso")

        // Send message from regular user
        let userMsg = Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = "hello from user",
            from = regularUser
        )
        let! _ = fixture.SendMessage userMsg

        // Send message from channel
        let chMsg = Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = "spam from channel",
            from = Tg.channelBot(),
            senderChat = spamChannel
        )
        let! _ = fixture.SendMessage chMsg

        // Ban channel
        let! banResp =
            Tg.replyMsg(chMsg.Message, "/ban", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)

        // Channel message is banned
        let! chMsgBanned = fixture.MessageBanned chMsg.Message
        Assert.True(chMsgBanned, "Channel message should be banned")

        // Regular user's message is still in DB
        let! dbUserMsg = fixture.TryGetDbMessage userMsg.Message
        Assert.True(dbUserMsg.IsSome, "Regular user's message should still exist in DB")

        // Regular user is not banned
        let! userBanned = fixture.UserBanned regularUser.Id
        Assert.False(userBanned, "Regular user should NOT be banned")
    }

    [<Fact>]
    let ``Ban regular user does not affect channel's messages`` () = task {
        let regularUser = Tg.user()
        let someChannel = Tg.channel(username = "ch_iso_ban_user", title = "Some Channel")

        // Send message from regular user
        let userMsg = Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = "user spam",
            from = regularUser
        )
        let! _ = fixture.SendMessage userMsg

        // Send message from channel
        let chMsg = Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = "channel hello",
            from = Tg.channelBot(),
            senderChat = someChannel
        )
        let! _ = fixture.SendMessage chMsg

        // Ban regular user
        let! banResp =
            Tg.replyMsg(userMsg.Message, "/ban", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)

        // User message is banned
        let! userMsgBanned = fixture.MessageBanned userMsg.Message
        Assert.True(userMsgBanned, "User's message should be banned")

        // Channel's message is still in DB
        let! dbChMsg = fixture.TryGetDbMessage chMsg.Message
        Assert.True(dbChMsg.IsSome, "Channel's message should still exist in DB")

        // Channel is not banned
        let! channelBanned = fixture.UserBanned someChannel.Id
        Assert.False(channelBanned, "Channel should NOT be banned")
    }

    [<Fact>]
    let ``Multiple messages from same channel are all affected by ban`` () = task {
        let spamChannel = Tg.channel(username = "ch_multi", title = "Multi Spam Channel")

        // Send two messages from the same channel
        let msg1 = Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = "spam message 1",
            from = Tg.channelBot(),
            senderChat = spamChannel
        )
        let! _ = fixture.SendMessage msg1

        let msg2 = Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = "spam message 2",
            from = Tg.channelBot(),
            senderChat = spamChannel
        )
        let! _ = fixture.SendMessage msg2

        // Ban channel via first message
        let! banResp =
            Tg.replyMsg(msg1.Message, "/ban", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)

        // First message is banned
        let! msg1Banned = fixture.MessageBanned msg1.Message
        Assert.True(msg1Banned, "First message should be banned")

        // Both messages were stored with the same channel ID
        let! dbMsg1 = fixture.TryGetDbMessage msg1.Message
        let! dbMsg2 = fixture.TryGetDbMessage msg2.Message
        Assert.True(dbMsg1.IsSome, "First message should be in DB")
        Assert.True(dbMsg2.IsSome, "Second message should be in DB")
        Assert.Equal(spamChannel.Id, dbMsg1.Value.user_id)
        Assert.Equal(spamChannel.Id, dbMsg2.Value.user_id)
    }

    interface IClassFixture<MlAwaitFixture>
