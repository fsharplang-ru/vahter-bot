module PingTests

open System.Net
open VahterBanBot.Tests.ContainerTestBase
open VahterBanBot.Tests.TgMessageUtils
open Xunit

type PingTests(fixture: MlDisabledVahterTestContainers) =
    [<Fact>]
    let ``Message got recorded`` () = task {
        // chat from the allowed list
        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        
        // assert that the message is not in the db
        let! dbMsg = fixture.TryGetDbMessage msg.Message
        Assert.False dbMsg.IsSome
        
        // send the message to the bot
        let! resp = fixture.SendMessage msg
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        
        // assert that the message is in the db
        let! dbMsg = fixture.TryGetDbMessage msg.Message
        Assert.True dbMsg.IsSome
    }

    [<Fact>]
    let ``Message not recorded if chat is not on the list`` () = task {
        // some random chat
        let msg = Tg.quickMsg(chat = Tg.chat())
        
        // assert that the message is not in the db
        let! dbMsg = fixture.TryGetDbMessage msg.Message
        Assert.False dbMsg.IsSome
        
        // send the message to the bot
        let! resp = fixture.SendMessage msg
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        
        // assert that the message is still not in the db
        let! dbMsg = fixture.TryGetDbMessage msg.Message
        Assert.False dbMsg.IsSome
    }

