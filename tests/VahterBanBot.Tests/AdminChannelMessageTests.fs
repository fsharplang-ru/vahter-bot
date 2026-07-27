module VahterBanBot.Tests.AdminChannelMessageTests

open System.Net
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

/// Covers persisting messages posted in the private admin (vahter) channel as
/// AdminChannelMessage events — a queryable feedback source distinct from MessageReceived
/// (see Types.fs `AdminChannelEvent` and Bot.fs `OnMessage`).
type AdminChannelMessageTests(fixture: MlDisabledVahterTestContainers) =

    [<Fact>]
    let ``free-form admin-channel message is persisted as AdminChannelMessage`` () = task {
        let update = Tg.quickMsg(text = "anyone seen this spammer before?", chat = fixture.AdminChannel, from = fixture.Vahters[0])
        let! resp = fixture.SendMessage update
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let msg = update.Message.Value
        let! recorded = fixture.TryGetAdminChannelMessage(msg.Chat.Id, msg.MessageId)
        Assert.True recorded.IsSome
        let recorded = recorded.Value
        Assert.Equal(msg.Chat.Id, recorded.chat_id)
        Assert.Equal(msg.MessageId, recorded.message_id)
        Assert.Equal(fixture.Vahters[0].Id, recorded.user_id)
        Assert.Equal("anyone seen this spammer before?", recorded.text)
    }

    [<Fact>]
    let ``vahter command in admin channel is persisted AND still executes`` () = task {
        do! fixture.ClearFakeCalls()
        let update = Tg.quickMsg(text = "/vahter help", chat = fixture.AdminChannel, from = fixture.Vahters[0])
        let! resp = fixture.SendMessage update
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        // command dispatch is unaffected — same reply as before this change
        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.True(calls |> Array.exists (fun c ->
            c.Body.Contains "\"chat_id\":-200" && c.Body.Contains "Vahter admin commands"))

        // AND the command itself was captured as feedback
        let msg = update.Message.Value
        let! recorded = fixture.TryGetAdminChannelMessage(msg.Chat.Id, msg.MessageId)
        Assert.True recorded.IsSome
        Assert.Equal("/vahter help", recorded.Value.text)
    }

    [<Fact>]
    let ``message in the all-logs channel is not persisted as AdminChannelMessage`` () = task {
        let update = Tg.quickMsg(text = "bot's own audit-log post", chat = fixture.AllLogsChannel, from = fixture.Vahters[0])
        let! resp = fixture.SendMessage update
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let msg = update.Message.Value
        let! recorded = fixture.TryGetAdminChannelMessage(msg.Chat.Id, msg.MessageId)
        Assert.True recorded.IsNone
    }

    [<Fact>]
    let ``normal monitored-chat message still produces MessageReceived and is unaffected`` () = task {
        let update = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        let! resp = fixture.SendMessage update
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let msg = update.Message.Value
        let! dbMsg = fixture.TryGetDbMessage msg
        Assert.True dbMsg.IsSome

        // and it must NOT also show up as admin-channel feedback
        let! recorded = fixture.TryGetAdminChannelMessage(msg.Chat.Id, msg.MessageId)
        Assert.True recorded.IsNone
    }
