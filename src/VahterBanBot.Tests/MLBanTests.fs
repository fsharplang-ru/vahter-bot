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
        let! msgBanned = fixture.MessageIsAutoBanned msgUpdate.Message
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
        let! msgBanned = fixture.MessageIsAutoBanned msgUpdate.Message
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
        let! msgBanned = fixture.MessageIsAutoBanned msgUpdate.Message
        Assert.False msgBanned
    }
    
    [<Fact>]
    let ``Message is NOT autobanned if it has a stopword in specific chat`` () = task {
        // record a message, where 2 is in a training set as spam word
        // ChatsToMonitor[1] does have a stopword 2
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[1], text = "2222222")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoBanned msgUpdate.Message
        Assert.False msgBanned
    }
    
    [<Fact>]
    let ``Message is NOT autobanned if it is a known false-positive spam`` () = task {
        // record a message, where 3 is in a training set as spam word
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "a")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoBanned msgUpdate.Message
        Assert.False msgBanned
    }
    
    [<Fact>]
    let ``Message IS autobanned if it is a known false-negative spam`` () = task {
        // record a message, where 3 is in a training set as false negative spam word
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "3333333")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoBanned msgUpdate.Message
        Assert.True msgBanned
    }
    
    [<Fact>]
    let ``If message got auto-deleted we can mark it as false-positive with a button click`` () = task {
        // record a message, where 2 is in a training set as spam word
        // ChatsToMonitor[0] doesn't have stopwords
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "7777777")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoBanned msgUpdate.Message
        Assert.True msgBanned
        // assert it is not false-positive
        let! isFalsePositive = fixture.IsMessageFalsePositive msgUpdate.Message
        Assert.False isFalsePositive
        
        // send a callback to mark it as false-positive
        let! callbackId = fixture.GetCallbackId msgUpdate.Message "NotASpam"
        let msgCallback = Tg.callback(string callbackId, from = fixture.Vahters[0])
        let! _ = fixture.SendMessage msgCallback
        
        // assert it is false-positive
        let! isFalsePositive = fixture.IsMessageFalsePositive msgUpdate.Message
        Assert.True isFalsePositive
    }
    
    [<Fact>]
    let ``Only vahter can press THE BUTTON(s)`` () = task {
        // record a message, where 2 is in a training set as spam word
        // ChatsToMonitor[0] doesn't have stopwords
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "66666666")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoBanned msgUpdate.Message
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

    interface IAssemblyFixture<VahterTestContainers>
    interface IClassFixture<MlAwaitFixture>
