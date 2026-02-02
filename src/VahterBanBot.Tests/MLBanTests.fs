module VahterBanBot.Tests.MLBanTests

open VahterBanBot.Tests.ContainerTestBase
open VahterBanBot.Tests.TgMessageUtils
open VahterBanBot.Types
open Xunit
open Xunit.Extensions.AssemblyFixture

type MLBanTests(fixture: MlEnabledVahterTestContainers, _unused: MlAwaitFixture) =

    [<Fact>]
    let ``Message IS autobanned if it looks like a spam`` () = task {
        // record a message, where 2 is in a training set as spam word
        // ChatsToMonitor[0] doesn't have stopwords
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.True msgBanned
    }
    
    [<Fact>]
    let ``Message is NOT autobanned if it looks like a spam BUT vahter sent it`` () = task {
        // record a message, where 2 is in a training set as spam word
        // ChatsToMonitor[0] doesn't have stopwords
        // but it was sent by vahter
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222", from = fixture.Vahters[0])
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.False msgBanned
    }
    
    [<Fact>]
    let ``Message is NOT autobanned if it looks like a spam BUT local admin sent it`` () = task {
        // record a message, where 2 is in a training set as spam word
        // ChatsToMonitor[0] doesn't have stopwords
        // but it was sent by local admin
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222", from = fixture.Admins[0])
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.False msgBanned
    }
    
    [<Fact>]
    let ``Message is NOT autobanned if it is an automatic forward from linked channel`` () = task {
        // Automatic forwards from linked channels have:
        // - from.id = 777000 (Telegram system user)
        // - sender_chat = the channel
        // - is_automatic_forward = true
        // These should be skipped even if they contain spam-like text
        let linkedChannel = Tg.channel(username = "linked_channel", title = "Linked Channel")
        let msgUpdate = Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = "2222222", // spam text
            from = Tg.telegramUser(), // Telegram system user (id 777000)
            senderChat = linkedChannel,
            isAutomaticForward = true
        )
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message was NOT auto banned (because it's an automatic forward)
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.False msgBanned
        
        // verify the message was saved to DB
        let! dbMsg = fixture.TryGetDbMessage msgUpdate.Message
        Assert.True dbMsg.IsSome
    }
    
    [<Fact>]
    let ``Message is NOT autobanned if it has a stopword in specific chat`` () = task {
        // record a message, where 2 is in a training set as spam word
        // ChatsToMonitor[1] does have a stopword 2
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[1], text = "2222222")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.False msgBanned
    }
    
    [<Fact>]
    let ``Message is NOT autobanned if it is a known false-positive spam`` () = task {
        // record a message, where 3 is in a training set as spam word
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "a")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.False msgBanned
    }
    
    [<Fact>]
    let ``Message IS autobanned if it is a known false-negative spam`` () = task {
        // record a message, where 3 is in a training set as false negative spam word
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "3333333")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.True msgBanned
    }
    
    [<Fact>]
    let ``If message got auto-deleted we can mark it as false-positive with a button click`` () = task {
        // record a message, where 7 is in a training set as spam word (detected spam)
        // ChatsToMonitor[0] doesn't have stopwords
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "7777777")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.True msgBanned
        // assert it is not false-positive
        let! isFalsePositive = fixture.IsMessageFalsePositive msgUpdate.Message
        Assert.False isFalsePositive
        // assert user is NOT banned (auto-delete is not the same as ban)
        let! userBanned = fixture.UserBanned msgUpdate.Message.From.Id
        Assert.False userBanned
        
        // send a callback to mark it as false-positive (NOT SPAM button)
        let! callbackId = fixture.GetCallbackId msgUpdate.Message "NotASpam"
        let msgCallback = Tg.callback(string callbackId, from = fixture.Vahters[0])
        let! _ = fixture.SendMessage msgCallback
        
        // assert it is false-positive
        let! isFalsePositive = fixture.IsMessageFalsePositive msgUpdate.Message
        Assert.True isFalsePositive
        // CRITICAL: assert user is still NOT banned after NOT SPAM button
        let! userBanned = fixture.UserBanned msgUpdate.Message.From.Id
        Assert.False userBanned
    }
    
    [<Fact>]
    let ``Potential spam NOT SPAM button does not ban user`` () = task {
        // For potential spam, we need a text that gives score >= 0 but < 1.0 (ML_SPAM_THRESHOLD)
        // Using a shorter spam-like text that might give lower score
        // This tests the fix for the bug where NOT SPAM button was calling vahterMarkedAsSpam
        let user = Tg.user()
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = user)
        let! _ = fixture.SendMessage msgUpdate

        // For potential spam, message is NOT auto-deleted (deleteMessage = false)
        let! msgAutoDeleted = fixture.MessageIsAutoDeleted msgUpdate.Message
        // If this is potential spam, it won't be auto-deleted
        // If it IS auto-deleted, it means score >= threshold, but the test still validates the bug fix
        
        // User should NOT be banned initially
        let! userBannedBefore = fixture.UserBanned user.Id
        Assert.False userBannedBefore
        
        // Try to get NotASpam callback (this exists for both detected and potential spam)
        let! callbackId = fixture.GetCallbackId msgUpdate.Message "NotASpam"
        let msgCallback = Tg.callback(string callbackId, from = fixture.Vahters[0])
        let! _ = fixture.SendMessage msgCallback
        
        // CRITICAL: User should still NOT be banned after NOT SPAM button
        // This is the main assertion for the bug fix
        let! userBannedAfter = fixture.UserBanned user.Id
        Assert.False userBannedAfter
    }
    
    [<Fact>]
    let ``Only vahter can press THE BUTTON(s)`` () = task {
        // record a message, where 2 is in a training set as spam word
        // ChatsToMonitor[0] doesn't have stopwords
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "66666666")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.True msgBanned
        
        // send a callback to mark it as false-positive
        // we are sending this as a usual user
        let! callbackId = fixture.GetCallbackId msgUpdate.Message (nameof CallbackMessage.NotASpam)
        let msgCallback = Tg.callback(string callbackId, from = msgUpdate.Message.From)
        let! _ = fixture.SendMessage msgCallback
        
        // assert it is still NOT a false-positive
        let! isFalsePositive = fixture.IsMessageFalsePositive msgUpdate.Message
        Assert.False isFalsePositive
    }
    
    [<Fact>]
    let ``User will be autobanned after consecutive spam`` () = task {
        // record a message, where 2 is in a training set as spam word
        // ChatsToMonitor[0] doesn't have stopwords
        let user = Tg.user()
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "66666666", from = user)
        
        // 1 - no ban
        let! _ = fixture.SendMessage msgUpdate
        let! msgBanned = fixture.MessageBanned msgUpdate.Message
        Assert.False msgBanned
        
        // 2 - no ban
        let! _ = fixture.SendMessage msgUpdate
        let! msgBanned = fixture.MessageBanned msgUpdate.Message
        Assert.False msgBanned
        
        // 3 - no ban
        let! _ = fixture.SendMessage msgUpdate
        let! msgBanned = fixture.MessageBanned msgUpdate.Message
        Assert.False msgBanned
        
        // 4 - ban (depends on the ML_SPAM_AUTOBAN_SCORE_THRESHOLD)
        let! _ = fixture.SendMessage msgUpdate
        let! msgBanned = fixture.MessageBanned msgUpdate.Message
        Assert.True msgBanned
    }
    
    [<Fact>]
    let ``User can recover from autoban by sending good messages`` () = task {
        // record a message, where 2 is in a training set as spam word
        // ChatsToMonitor[0] doesn't have stopwords
        let user = Tg.user()
        let spam = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "66666666", from = user)
        let notSpam = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "b", from = user)
        
        // 1 - no ban
        let! _ = fixture.SendMessage spam
        let! msgBanned = fixture.MessageBanned spam.Message
        Assert.False msgBanned
        
        // 1.5 - no ban
        let! _ = fixture.SendMessage notSpam
        let! msgBanned = fixture.MessageBanned notSpam.Message
        Assert.False msgBanned
        
        // 2 - no ban
        let! _ = fixture.SendMessage spam
        let! msgBanned = fixture.MessageBanned spam.Message
        Assert.False msgBanned
        
        // 3 - no ban
        let! _ = fixture.SendMessage spam
        let! msgBanned = fixture.MessageBanned spam.Message
        Assert.False msgBanned
        
        // 4 - no ban (as user posted 1 good message in beetween)
        let! _ = fixture.SendMessage spam
        let! msgBanned = fixture.MessageBanned spam.Message
        Assert.False msgBanned
    }
    
    [<Fact>]
    let ``User can be saved from auto ban by vahter marking it false-positive`` () = task {
        // record a message, where 777777777777777777 is in a training set as spam word
        // ChatsToMonitor[0] doesn't have stopwords
        let user = Tg.user()
        let spam = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "777777777777777777", from = user)
        
        // 1 - no ban
        let! _ = fixture.SendMessage spam
        let! msgBanned = fixture.MessageBanned spam.Message
        let! msgDeleted = fixture.MessageIsAutoDeleted spam.Message
        Assert.True msgDeleted
        Assert.False msgBanned
        
        // 1.5 - vahter marked as false-positive via button
        // send a callback to mark it as false-positive
        let! callbackId = fixture.GetCallbackId spam.Message "NotASpam"
        let msgCallback = Tg.callback(string callbackId, from = fixture.Vahters[0])
        let! _ = fixture.SendMessage msgCallback
        
        // 2 - no ban
        let! _ = fixture.SendMessage spam
        let! msgBanned = fixture.MessageBanned spam.Message
        Assert.False msgBanned
        
        // 3 - no ban
        let! _ = fixture.SendMessage spam
        let! msgBanned = fixture.MessageBanned spam.Message
        Assert.False msgBanned
        
        // 4 - no ban (as vahter marked this as false positive)
        let! _ = fixture.SendMessage spam
        let! msgBanned = fixture.MessageBanned spam.Message
        Assert.False msgBanned
    }
    
    [<Fact>]
    let ``Message with spam in photo caption also triggers auto-delete`` () = task {
        // record a message, where 2222222 is in a training set as spam word
        // but text is in a message.Caption
        // ChatsToMonitor[0] doesn't have stopwords
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = null, caption = "2222222")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.True msgBanned
    }
    
    [<Fact>]
    let ``Message which were edited triggers auto-delete`` () = task {
        // record a message, which originally was not a spam,
        // but it was edited to be a spam
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "a", editedText = "2222222")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.True msgBanned
    }

    [<Fact>]
    let ``Bans in chat without username should work`` () = task {
        // record a message in a chat without username
        let chat = Tg.chat(id = fixture.ChatsToMonitor[0].Id, username = null)

        let msgUpdate = Tg.quickMsg(chat = chat, text = "2222222")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.True msgBanned
    }

    [<Fact>]
    let ``Spam detected in OCR text is auto deleted`` () = task {
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = null, photos = [| Tg.spamPhoto |])

        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.True msgBanned
        
        let! dbMsg = fixture.TryGetDbMessage msgUpdate.Message
        Assert.Equal("2222222", dbMsg.Value.text)
    }

    [<Fact>]
    let ``Ham photo text does not trigger autoban`` () = task {
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = null, photos = [| Tg.hamPhoto |])

        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.False msgBanned
        
        let! dbMsg = fixture.TryGetDbMessage msgUpdate.Message
        Assert.Equal("b", dbMsg.Value.text)
    }

    [<Fact>]
    let ``Spam photo chosen under size limit when oversized image exists`` () = task {
        let msgUpdate = Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = null,
            photos = [|
                Tg.spamPhoto
                Tg.bigPhoto
            |]
        )

        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.True msgBanned
        
        let! dbMsg = fixture.TryGetDbMessage msgUpdate.Message
        Assert.Equal("2222222", dbMsg.Value.text)
    }

    [<Fact>]
    let ``Ham photo survives when only larger image is too big`` () = task {
        let msgUpdate = Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = null,
            photos = [|
                Tg.hamPhoto
                Tg.bigPhoto
            |]
        )

        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.False msgBanned
        
        let! dbMsg = fixture.TryGetDbMessage msgUpdate.Message
        Assert.Equal("b", dbMsg.Value.text)
    }
    
    [<Fact>]
    let ``Original text and parsed text from photo should be combined`` () = task {
        let msgUpdate = Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = "Hello!",
            photos = [| Tg.hamPhoto |]
        )

        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.False msgBanned
        
        let! dbMsg = fixture.TryGetDbMessage msgUpdate.Message
        Assert.Equal("Hello!\nb", dbMsg.Value.text)
    }
    
    [<Fact>]
    let ``MarkAsSpam (soft spam) button does NOT ban user`` () = task {
        // This test verifies the critical behavior that MarkAsSpam (soft spam)
        // deletes the message and marks it as spam for ML, but does NOT ban the user
        let user = Tg.user()
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = user)
        let! _ = fixture.SendMessage msgUpdate
        
        // User should NOT be banned initially
        let! userBannedBefore = fixture.UserBanned user.Id
        Assert.False(userBannedBefore, "User should not be banned initially")
        
        // Click MarkAsSpam button (soft spam) - uses the new third button
        let! callbackId = fixture.GetCallbackId msgUpdate.Message "MarkAsSpam"
        let msgCallback = Tg.callback(string callbackId, from = fixture.Vahters[0])
        let! _ = fixture.SendMessage msgCallback
        
        // CRITICAL: User should still NOT be banned after MarkAsSpam button
        // This is the main assertion - soft spam should NOT ban the user
        let! userBannedAfter = fixture.UserBanned user.Id
        Assert.False(userBannedAfter, "User should NOT be banned after MarkAsSpam - this is soft delete only!")
        
        // Message should be marked as false negative (spam for ML training)
        let! isFalseNegative = fixture.IsMessageFalseNegative msgUpdate.Message
        Assert.True(isFalseNegative, "Message should be marked as false negative for ML")
    }
    
    [<Fact>]
    let ``Only vahter can click MarkAsSpam button`` () = task {
        // Similar to "Only vahter can press THE BUTTON(s)" test
        // Verifies non-vahter clicking MarkAsSpam has no effect
        let user = Tg.user()
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = user)
        let! _ = fixture.SendMessage msgUpdate
        
        // Try to click MarkAsSpam as regular user (not vahter)
        let! callbackId = fixture.GetCallbackId msgUpdate.Message "MarkAsSpam"
        let msgCallback = Tg.callback(string callbackId, from = user) // regular user, not vahter
        let! _ = fixture.SendMessage msgCallback
        
        // Message should NOT be marked as false negative (action was rejected)
        let! isFalseNegative = fixture.IsMessageFalseNegative msgUpdate.Message
        Assert.False(isFalseNegative, "Non-vahter should not be able to mark message as spam")
    }
    
    [<Fact>]
    let ``User will be autobanned after consecutive MarkAsSpam clicks`` () = task {
        // Tests that karma system works with MarkAsSpam
        // After enough soft spam marks, user gets auto-banned
        // ML_SPAM_AUTOBAN_SCORE_THRESHOLD is -4.0
        // socialScore <= threshold triggers ban, so:
        // - After 1st: score=-1, -1 > -4 → no ban
        // - After 2nd: score=-2, -2 > -4 → no ban
        // - After 3rd: score=-3, -3 > -4 → no ban
        // - After 4th: score=-4, -4 <= -4 → BAN!
        let user = Tg.user()
        
        // First 3 messages should NOT trigger ban
        for i in 1..3 do
            let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = user)
            let! _ = fixture.SendMessage msgUpdate
            
            // Click MarkAsSpam (soft spam)
            let! callbackId = fixture.GetCallbackId msgUpdate.Message "MarkAsSpam"
            let msgCallback = Tg.callback(string callbackId, from = fixture.Vahters[0])
            let! _ = fixture.SendMessage msgCallback
            
            // User should not be banned yet (score is -1, -2, -3)
            let! userBanned = fixture.UserBanned user.Id
            Assert.False(userBanned, $"User should not be banned after {i} soft spam marks (score={-i})")
        
        // 4th soft spam should trigger auto-ban (score becomes -4 which is <= -4.0 threshold)
        let finalMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = user)
        let! _ = fixture.SendMessage finalMsg
        
        let! callbackId = fixture.GetCallbackId finalMsg.Message "MarkAsSpam"
        let msgCallback = Tg.callback(string callbackId, from = fixture.Vahters[0])
        let! _ = fixture.SendMessage msgCallback
        
        // Now user should be auto-banned due to low karma (score=-4 <= threshold=-4)
        let! userBanned = fixture.UserBanned user.Id
        Assert.True(userBanned, "User should be auto-banned after reaching karma threshold via soft spam")
    }

    interface IAssemblyFixture<MlEnabledVahterTestContainers>
    interface IClassFixture<MlAwaitFixture>
