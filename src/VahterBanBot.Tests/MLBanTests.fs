module VahterBanBot.Tests.MLBanTests

open System.Net
open System.Threading.Tasks
open VahterBanBot.Tests.ContainerTestBase
open VahterBanBot.Tests.TgMessageUtils
open Xunit
open Xunit.Extensions.AssemblyFixture

type MLBanTests(fixture: VahterTestContainers) =

    [<Fact>]
    let ``Message IS autobanned if it looks like a spam`` () = task {
        // we assume 5 seconds is enough for model to train. Could be flaky
        do! Task.Delay 5000
        
        // record a message, where 2 is in a training set as spam word
        // ChatsToMonitor[0] doesn't have stopwords
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoBanned msgUpdate.Message
        Assert.True msgBanned
    }
    
    [<Fact>]
    let ``Message is NOT autobanned if it has a stopword in specific chat`` () = task {
        // we assume 5 seconds is enough for model to train. Could be flaky
        do! Task.Delay 5000
        
        // record a message, where 2 is in a training set as spam word
        // ChatsToMonitor[1] does have a stopword 2
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[1], text = "2")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoBanned msgUpdate.Message
        Assert.False msgBanned
    }
    
    [<Fact>]
    let ``Message is NOT autobanned if it is a known false-positive spam`` () = task {
        // we assume 5 seconds is enough for model to train. Could be flaky
        do! Task.Delay 5000
        
        // record a message, where 3 is in a training set as spam word
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "a")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoBanned msgUpdate.Message
        Assert.False msgBanned
    }
    
    [<Fact>]
    let ``Message IS autobanned if it is a known false-negative spam`` () = task {
        // we assume 5 seconds is enough for model to train. Could be flaky
        do! Task.Delay 5000
        
        // record a message, where 3 is in a training set as false negative spam word
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "3")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got auto banned
        let! msgBanned = fixture.MessageIsAutoBanned msgUpdate.Message
        Assert.True msgBanned
    }

    interface IAssemblyFixture<VahterTestContainers>
