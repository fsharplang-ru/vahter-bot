module VahterBanBot.Tests.BanTests

open System.Net
open VahterBanBot.Tests.ContainerTestBase
open VahterBanBot.Tests.TgMessageUtils
open Xunit

type BanTests(fixture: VahterTestContainers) =

    [<Fact>]
    let ``Vahter can ban on reply`` () = task {
        // record a message
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        let! _ = fixture.SendMessage msgUpdate

        // send the ban message
        let! banResp =
            Tg.replyMsg(msgUpdate.Message, "/ban", fixture.AdminUsers[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)
        
        // assert that the message got banned
        let! msgBanned = fixture.MessageBanned msgUpdate.Message
        Assert.True msgBanned
    }

    [<Fact>]
    let ``NON Vahter can't ban on reply`` () = task {
        // record a message
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        let! _ = fixture.SendMessage msgUpdate

        // send the ban message from a non-admin user
        let! banResp =
            Tg.replyMsg(msgUpdate.Message, "/ban", Tg.user())
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)
        
        // assert that the message NOT banned
        let! msgNotBanned = fixture.MessageBanned msgUpdate.Message
        Assert.False msgNotBanned
    }

    [<Fact>]
    let ``Vahter can't ban on reply in non-allowed chat`` () = task {
        // record a message in a random chat
        let msgUpdate = Tg.quickMsg(chat = Tg.chat())
        let! _ = fixture.SendMessage msgUpdate

        // send the ban message
        let! banResp =
            Tg.replyMsg(msgUpdate.Message, "/ban", fixture.AdminUsers[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)
        
        // assert that the message NOT banned
        let! msgNotBanned = fixture.MessageBanned msgUpdate.Message
        Assert.False msgNotBanned
    }

    [<Fact>]
    let ``Vahter can't ban another vahter`` () = task {
        // record a message in a random chat
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], from = fixture.AdminUsers[0])
        let! _ = fixture.SendMessage msgUpdate

        // send the ban message
        let! banResp =
            Tg.replyMsg(msgUpdate.Message, "/ban", fixture.AdminUsers[1])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)
        
        // assert that the message NOT banned
        let! msgNotBanned = fixture.MessageBanned msgUpdate.Message
        Assert.False msgNotBanned
    }
