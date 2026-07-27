module VahterBanBot.Tests.EntitiesScanTests

open Funogram.Telegram.Types
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

type EntitiesScanTestsWithoutMl(fixture: MlDisabledVahterTestContainers) =

    let buildMentionMessage (text: string) =
        Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = text,
            entities = [| MessageEntity.Create("mention", 0L, int64 text.Length) |])

    let buildTextMentionMessage (text: string) =
        Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = text,
            entities =
                [| MessageEntity.Create("text_mention", 0L, int64 text.Length, user = fixture.Admins[0]) |])

    [<Fact>]
    let ``Invisible mentions are auto deleted when ML is disabled`` () = task {
        let msgUpdate = buildMentionMessage "@\u200Buser"
        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.True msgBanned
    }

    [<Fact>]
    let ``Invisible text mentions are auto deleted when ML is disabled`` () = task {
        let msgUpdate = buildTextMentionMessage "\u200Buser"
        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.True msgBanned
    }

    [<Fact>]
    let ``Regular mentions are ignored when ML is disabled`` () = task {
        let msgUpdate = buildMentionMessage "@normaluser"
        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.False msgBanned
    }

    [<Fact>]
    let ``Messages without entities are ignored when ML is disabled`` () = task {
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "Just a message")
        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.False msgBanned
    }


type EntitiesScanTestsWithMl(fixture: MlEnabledVahterTestContainers, _unused: MlAwaitFixture) =

    let buildMentionMessage (text: string) =
        Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = text,
            entities = [| MessageEntity.Create("mention", 0L, int64 text.Length) |])

    let buildTextMentionMessage (text: string) =
        Tg.quickMsg(
            chat = fixture.ChatsToMonitor[0],
            text = text,
            entities =
                [| MessageEntity.Create("text_mention", 0L, int64 text.Length, user = fixture.Admins[0]) |])

    [<Fact>]
    let ``Invisible mentions are auto deleted when ML is enabled`` () = task {
        let msgUpdate = buildMentionMessage "@\u200Buser"
        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.True msgBanned
    }

    [<Fact>]
    let ``Invisible text mentions are auto deleted when ML is enabled`` () = task {
        let msgUpdate = buildTextMentionMessage "\u200Buser"
        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.True msgBanned
    }

    [<Fact>]
    let ``Regular mentions are ignored when ML is enabled`` () = task {
        let msgUpdate = buildMentionMessage "@normaluser"
        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.False msgBanned
    }

    [<Fact>]
    let ``Messages without entities are ignored when ML is enabled`` () = task {
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "Just a message", from = fixture.Vahters[0])
        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message.Value
        Assert.False msgBanned
    }

    interface IClassFixture<MlAwaitFixture>
