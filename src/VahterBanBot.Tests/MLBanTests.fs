module VahterBanBot.Tests.MLBanTests

open VahterBanBot.Tests.ContainerTestBase
open VahterBanBot.Tests.TgMessageUtils
open VahterBanBot.Types
open Xunit
open Xunit.Extensions.AssemblyFixture

type MLBanTests(fixture: VahterTestContainers, _unused: MlAwaitFixture) =

    [<Fact>]
    let ``Message IS autobanned if it looks like a spam`` () = task {
        // record a message, where 2 is in a training set as spam word
        // ChatsToMonitor[0] doesn't have stopwords
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
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
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
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
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.False msgBanned
    }
    
    [<Fact>]
    let ``Message is NOT autobanned if it has a stopword in specific chat`` () = task {
        // record a message, where 2 is in a training set as spam word
        // ChatsToMonitor[1] does have a stopword 2
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[1], text = "2222222")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.False msgBanned
    }
    
    [<Fact>]
    let ``Message is NOT autobanned if it is a known false-positive spam`` () = task {
        // record a message, where 3 is in a training set as spam word
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "a")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.False msgBanned
    }
    
    [<Fact>]
    let ``Message IS autobanned if it is a known false-negative spam`` () = task {
        // record a message, where 3 is in a training set as false negative spam word
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "3333333")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.True msgBanned
    }
    
    [<Fact>]
    let ``If message got auto-deleted we can mark it as false-positive with a button click`` () = task {
        // record a message, where 2 is in a training set as spam word
        // ChatsToMonitor[0] doesn't have stopwords
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "7777777")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.True msgBanned
        // assert it is not false-positive
        let! isFalsePositive = fixture.IsMessageFalsePositive msgUpdate.Message.Value
        Assert.False isFalsePositive
        
        // send a callback to mark it as false-positive
        let! callbackId = fixture.GetCallbackId msgUpdate.Message.Value "NotASpam"
        let msgCallback = Tg.callback(string callbackId, from = fixture.Vahters[0])
        let! _ = fixture.SendMessage msgCallback
        
        // assert it is false-positive
        let! isFalsePositive = fixture.IsMessageFalsePositive msgUpdate.Message.Value
        Assert.True isFalsePositive
    }
    
    [<Fact>]
    let ``Only vahter can press THE BUTTON(s)`` () = task {
        // record a message, where 2 is in a training set as spam word
        // ChatsToMonitor[0] doesn't have stopwords
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "66666666")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.True msgBanned
        
        // send a callback to mark it as false-positive
        // we are sending this as a usual user
        let! callbackId = fixture.GetCallbackId msgUpdate.Message.Value (nameof CallbackMessage.NotASpam)
        let msgCallback = Tg.callback(string callbackId, from = msgUpdate.Message.Value.From.Value)
        let! _ = fixture.SendMessage msgCallback
        
        // assert it is still NOT a false-positive
        let! isFalsePositive = fixture.IsMessageFalsePositive msgUpdate.Message.Value
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
        let! msgBanned = fixture.MessageBanned msgUpdate.Message.Value
        Assert.False msgBanned
        
        // 2 - no ban
        let! _ = fixture.SendMessage msgUpdate
        let! msgBanned = fixture.MessageBanned msgUpdate.Message.Value
        Assert.False msgBanned
        
        // 3 - no ban
        let! _ = fixture.SendMessage msgUpdate
        let! msgBanned = fixture.MessageBanned msgUpdate.Message.Value
        Assert.False msgBanned
        
        // 4 - ban (depends on the ML_SPAM_AUTOBAN_SCORE_THRESHOLD)
        let! _ = fixture.SendMessage msgUpdate
        let! msgBanned = fixture.MessageBanned msgUpdate.Message.Value
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
        let! msgBanned = fixture.MessageBanned spam.Message.Value
        Assert.False msgBanned
        
        // 1.5 - no ban
        let! _ = fixture.SendMessage notSpam
        let! msgBanned = fixture.MessageBanned notSpam.Message.Value
        Assert.False msgBanned
        
        // 2 - no ban
        let! _ = fixture.SendMessage spam
        let! msgBanned = fixture.MessageBanned spam.Message.Value
        Assert.False msgBanned
        
        // 3 - no ban
        let! _ = fixture.SendMessage spam
        let! msgBanned = fixture.MessageBanned spam.Message.Value
        Assert.False msgBanned
        
        // 4 - no ban (as user posted 1 good message in beetween)
        let! _ = fixture.SendMessage spam
        let! msgBanned = fixture.MessageBanned spam.Message.Value
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
        let! msgBanned = fixture.MessageBanned spam.Message.Value
        let! msgDeleted = fixture.MessageIsAutoDeleted spam.Message.Value
        Assert.True msgDeleted
        Assert.False msgBanned
        
        // 1.5 - vahter marked as false-positive via button
        // send a callback to mark it as false-positive
        let! callbackId = fixture.GetCallbackId spam.Message.Value "NotASpam"
        let msgCallback = Tg.callback(string callbackId, from = fixture.Vahters[0])
        let! _ = fixture.SendMessage msgCallback
        
        // 2 - no ban
        let! _ = fixture.SendMessage spam
        let! msgBanned = fixture.MessageBanned spam.Message.Value
        Assert.False msgBanned
        
        // 3 - no ban
        let! _ = fixture.SendMessage spam
        let! msgBanned = fixture.MessageBanned spam.Message.Value
        Assert.False msgBanned
        
        // 4 - no ban (as vahter marked this as false positive)
        let! _ = fixture.SendMessage spam
        let! msgBanned = fixture.MessageBanned spam.Message.Value
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
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.True msgBanned
    }
    
    [<Fact>]
    let ``Message which were edited triggers auto-delete`` () = task {
        // record a message, which originally was not a spam,
        // but it was edited to be a spam
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "a", editedText = "2222222")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.True msgBanned
    }

    interface IAssemblyFixture<VahterTestContainers>
    interface IClassFixture<MlAwaitFixture>
