module VahterBanBot.Tests.BanTests

open System.Net
open VahterBanBot.Tests.ContainerTestBase
open VahterBanBot.Tests.TgMessageUtils
open VahterBanBot.Utils
open Xunit
open Xunit.Extensions.AssemblyFixture

type BanTests(fixture: VahterTestContainers) =

    [<Fact>]
    let ``Vahter can ban on reply`` () = task {
        // record a message
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        let! _ = fixture.SendMessage msgUpdate

        // send the ban message
        let! banResp =
            Tg.replyMsg(msgUpdate.Message.Value, "/ban", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)
        
        // assert that the message got banned
        let! msgBanned = fixture.MessageBanned msgUpdate.Message.Value
        Assert.True msgBanned
    }

    [<Fact>]
    let ``NON Vahter can't ban on reply`` () = task {
        // record a message
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        let! _ = fixture.SendMessage msgUpdate

        // send the ban message from a non-admin user
        let! banResp =
            Tg.replyMsg(msgUpdate.Message.Value, "/ban", Tg.user())
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)
        
        // assert that the message NOT banned
        let! msgNotBanned = fixture.MessageBanned msgUpdate.Message.Value
        Assert.False msgNotBanned
    }

    [<Fact>]
    let ``Vahter can't ban on reply in non-allowed chat`` () = task {
        // record a message in a random chat
        let msgUpdate = Tg.quickMsg(chat = Tg.chat())
        let! _ = fixture.SendMessage msgUpdate

        // send the ban message
        let! banResp =
            Tg.replyMsg(msgUpdate.Message.Value, "/ban", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)
        
        // assert that the message NOT banned
        let! msgNotBanned = fixture.MessageBanned msgUpdate.Message.Value
        Assert.False msgNotBanned
    }

    [<Fact>]
    let ``Vahter can't ban another vahter`` () = task {
        // record a message in a random chat
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], from = fixture.Vahters[0])
        let! _ = fixture.SendMessage msgUpdate

        // send the ban message
        let! banResp =
            Tg.replyMsg(msgUpdate.Message.Value, "/ban", fixture.Vahters[1])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)
        
        // assert that the message NOT banned
        let! msgNotBanned = fixture.MessageBanned msgUpdate.Message.Value
        Assert.False msgNotBanned
    }

    [<Fact>]
    let ``Vahter can unban user`` () = task {
        // record a message
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        let! _ = fixture.SendMessage msgUpdate

        // send the ban message
        let! banResp =
            Tg.replyMsg(msgUpdate.Message.Value, "/ban", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)
        
        // assert that the message got banned
        let! msgBanned = fixture.MessageBanned msgUpdate.Message.Value
        Assert.True msgBanned
        
        // send the unban message from another vahter
        let! banResp =
            Tg.quickMsg($"/unban {msgUpdate.Message.Value.FromId}", chat = fixture.ChatsToMonitor[0], from = fixture.Vahters[1])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)
        
        // assert that the message no longer banned
        let! msgBanned = fixture.MessageBanned msgUpdate.Message.Value
        Assert.False msgBanned
    }

    [<Fact>]
    let ``Only Vahter can unban user`` () = task {
        // record a message
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        let! _ = fixture.SendMessage msgUpdate

        // send the ban message
        let! banResp =
            Tg.replyMsg(msgUpdate.Message.Value, "/ban", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)
        
        // assert that the message got banned
        let! msgBanned = fixture.MessageBanned msgUpdate.Message.Value
        Assert.True msgBanned
        
        // send the unban message from a random user
        let! banResp =
            Tg.quickMsg($"/unban {msgUpdate.Message.Value.FromId}", chat = fixture.ChatsToMonitor[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)
        
        // assert that the message still banned
        let! msgBanned = fixture.MessageBanned msgUpdate.Message.Value
        Assert.True msgBanned
    }

    interface IAssemblyFixture<VahterTestContainers>
