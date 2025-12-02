module VahterBanBot.Tests.EntitiesScanTests

open Telegram.Bot.Types
open Telegram.Bot.Types.Enums
open VahterBanBot.Tests.ContainerTestBase
open VahterBanBot.Tests.TgMessageUtils
open Xunit
open Xunit.Extensions.AssemblyFixture

type EntitiesScanTestsWithoutMl(fixture: MlDisabledVahterTestContainers) =

    let buildMentionMessage text =
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = text)
        msgUpdate.Message.Entities <- [| MessageEntity(Type = MessageEntityType.Mention, Offset = 0, Length = text.Length) |]
        msgUpdate

    let buildTextMentionMessage text =
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = text)
        msgUpdate.Message.Entities <-
            [| MessageEntity(Type = MessageEntityType.TextMention,
                             Offset = 0,
                             Length = text.Length,
                             User = fixture.Admins[0]) |]
        msgUpdate

    [<Fact>]
    let ``Invisible mentions are auto deleted when ML is disabled`` () = task {
        let msgUpdate = buildMentionMessage "@\u200Buser"
        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.True msgBanned
    }

    [<Fact>]
    let ``Invisible text mentions are auto deleted when ML is disabled`` () = task {
        let msgUpdate = buildTextMentionMessage "\u200Buser"
        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.True msgBanned
    }

    [<Fact>]
    let ``Regular mentions are ignored when ML is disabled`` () = task {
        let msgUpdate = buildMentionMessage "@normaluser"
        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.False msgBanned
    }

    [<Fact>]
    let ``Messages without entities are ignored when ML is disabled`` () = task {
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "Just a message")
        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.False msgBanned
    }

    interface IAssemblyFixture<MlDisabledVahterTestContainers>

type EntitiesScanTestsWithMl(fixture: MlEnabledVahterTestContainers, _unused: MlAwaitFixture) =

    let buildMentionMessage text =
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = text)
        msgUpdate.Message.Entities <- [| MessageEntity(Type = MessageEntityType.Mention, Offset = 0, Length = text.Length) |]
        msgUpdate

    let buildTextMentionMessage text =
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = text)
        msgUpdate.Message.Entities <-
            [| MessageEntity(Type = MessageEntityType.TextMention,
                             Offset = 0,
                             Length = text.Length,
                             User = fixture.Admins[0]) |]
        msgUpdate

    [<Fact>]
    let ``Invisible mentions are auto deleted when ML is enabled`` () = task {
        let msgUpdate = buildMentionMessage "@\u200Buser"
        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.True msgBanned
    }

    [<Fact>]
    let ``Invisible text mentions are auto deleted when ML is enabled`` () = task {
        let msgUpdate = buildTextMentionMessage "\u200Buser"
        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.True msgBanned
    }

    [<Fact>]
    let ``Regular mentions are ignored when ML is enabled`` () = task {
        let msgUpdate = buildMentionMessage "@normaluser"
        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.False msgBanned
    }

    [<Fact>]
    let ``Messages without entities are ignored when ML is enabled`` () = task {
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "Just a message", from = fixture.Vahters[0])
        let! _ = fixture.SendMessage msgUpdate

        let! msgBanned = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.False msgBanned
    }

    interface IAssemblyFixture<MlEnabledVahterTestContainers>
    interface IClassFixture<MlAwaitFixture>
