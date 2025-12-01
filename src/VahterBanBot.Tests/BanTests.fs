module VahterBanBot.Tests.BanTests

open System.Net
open VahterBanBot.Tests.ContainerTestBase
open VahterBanBot.Tests.TgMessageUtils
open Xunit
open Xunit.Extensions.AssemblyFixture

type BanTests(fixture: MlDisabledVahterTestContainers) =

    [<Fact>]
    let ``Vahter can ban on reply`` () = task {
        // record a message
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        let! _ = fixture.SendMessage msgUpdate

        // send the ban message
        let! banResp =
            Tg.replyMsg(msgUpdate.Message, "/ban", fixture.Vahters[0])
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
            Tg.replyMsg(msgUpdate.Message, "/ban", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)
        
        // assert that the message NOT banned
        let! msgNotBanned = fixture.MessageBanned msgUpdate.Message
        Assert.False msgNotBanned
    }

    [<Fact>]
    let ``Vahter can't ban another vahter`` () = task {
        // record a message in a random chat
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], from = fixture.Vahters[0])
        let! _ = fixture.SendMessage msgUpdate

        // send the ban message
        let! banResp =
            Tg.replyMsg(msgUpdate.Message, "/ban", fixture.Vahters[1])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)
        
        // assert that the message NOT banned
        let! msgNotBanned = fixture.MessageBanned msgUpdate.Message
        Assert.False msgNotBanned
    }

    [<Fact>]
    let ``Vahter can unban user`` () = task {
        // record a message
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        let! _ = fixture.SendMessage msgUpdate

        // send the ban message
        let! banResp =
            Tg.replyMsg(msgUpdate.Message, "/ban", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)
        
        // assert that the message got banned
        let! msgBanned = fixture.MessageBanned msgUpdate.Message
        Assert.True msgBanned
        
        // send the unban message from another vahter
        let! banResp =
            Tg.quickMsg($"/unban {msgUpdate.Message.From.Id}", chat = fixture.ChatsToMonitor[0], from = fixture.Vahters[1])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)
        
        // assert that the message no longer banned
        let! msgBanned = fixture.MessageBanned msgUpdate.Message
        Assert.False msgBanned
    }

    [<Fact>]
    let ``Only Vahter can unban user`` () = task {
        // record a message
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        let! _ = fixture.SendMessage msgUpdate

        // send the ban message
        let! banResp =
            Tg.replyMsg(msgUpdate.Message, "/ban", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)
        
        // assert that the message got banned
        let! msgBanned = fixture.MessageBanned msgUpdate.Message
        Assert.True msgBanned
        
        // send the unban message from a random user
        let! banResp =
            Tg.quickMsg($"/unban {msgUpdate.Message.From.Id}", chat = fixture.ChatsToMonitor[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)
        
        // assert that the message still banned
        let! msgBanned = fixture.MessageBanned msgUpdate.Message
        Assert.True msgBanned
    }
    
    [<Fact>]
    let ``Spammers with the same username will be banned`` () = task {
        // users with the same username, but different ids
        let user1 = Tg.user(username = "spammer")
        let user2 = Tg.user(username = "spammer")
        
        // record a message from USER1
        let msgUpdate1 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], from = user1)
        let! _ = fixture.SendMessage msgUpdate1

        // send the ban message
        let! banResp1 =
            Tg.replyMsg(msgUpdate1.Message, "/ban", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp1.StatusCode)
        
        // assert that the message from USER1 got banned
        let! msgBanned = fixture.MessageBanned msgUpdate1.Message
        Assert.True msgBanned
        
        // record a message from USER2
        let msgUpdate2 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], from = user2)
        let! _ = fixture.SendMessage msgUpdate2
        
        // send the ban message
        let! banResp2 =
            Tg.replyMsg(msgUpdate2.Message, "/ban", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp2.StatusCode)

        // assert that the message from USER2 got banned
        let! msgBanned = fixture.MessageBanned msgUpdate2.Message
        Assert.True msgBanned
    }

    interface IAssemblyFixture<MlDisabledVahterTestContainers>
