module VahterBanBot.Tests.JoinMessageTests

open System
open System.Net
open System.Text.Json
open System.Threading.Tasks
open BotTestInfra
open VahterBanBot.Tests.ContainerTestBase
open Xunit
open Dapper
open Npgsql

type JoinMessageTests(fixture: MlDisabledVahterTestContainers) =

    let deletedJoinCount (message: Funogram.Telegram.Types.Message) = task {
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        return! conn.ExecuteScalarAsync<int>(
            """SELECT count(*) FROM event
               WHERE stream_id = @streamId AND event_type = 'JoinNotificationDeleted'
                 AND (data->>'chatId')::bigint = @chatId AND (data->>'messageId')::bigint = @messageId""",
            {| streamId = $"join-notification:{message.Chat.Id}:{message.MessageId}"
               chatId = message.Chat.Id; messageId = message.MessageId |})
    }

    let assertDeleted expected (update: Funogram.Telegram.Types.Update) = task {
        let! response = fixture.SendMessage update
        Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        let! calls = fixture.GetFakeCalls "deleteMessage"
        let message = update.Message.Value
        let deleted =
            calls |> Array.exists (fun call ->
                use body = JsonDocument.Parse call.Body
                body.RootElement.GetProperty("chat_id").GetInt64() = message.Chat.Id
                && body.RootElement.GetProperty("message_id").GetInt64() = message.MessageId)
        Assert.Equal(expected, deleted)
        let! recorded = deletedJoinCount message
        Assert.Equal((if expected then 1 else 0), recorded)
    }

    let joinMessage chat count =
        let update = Tg.quickMsg(chat = chat, text = null)
        { update with
            Message = Some { update.Message.Value with NewChatMembers = Some (Array.init count (fun _ -> Tg.user())) } }

    [<Fact>]
    let ``Join deletion defaults off and hot reload opts in only listed monitored chats`` () = task {
        let chat = fixture.ChatsToMonitor[0]
        let! setting = fixture.GetBotSetting "DELETE_JOIN_MESSAGES_IN_CHATS"
        Assert.Equal(Some "[]", setting)
        do! assertDeleted false (joinMessage chat 1)

        let unmonitored = Tg.chat(id = -999999L)
        do! fixture.SetBotSetting("DELETE_JOIN_MESSAGES_IN_CHATS", JsonSerializer.Serialize [|chat.Id; unmonitored.Id|])
        do! fixture.ReloadSettings()

        for count in [1; 3] do
            let update = joinMessage chat count
            do! assertDeleted true update
            do! assertDeleted true update
            let! stored = fixture.TryGetDbMessage update.Message.Value
            Assert.True(stored.IsNone)
        do! assertDeleted false (joinMessage fixture.ChatsToMonitor[1] 1)
        do! assertDeleted false (joinMessage unmonitored 1)
        do! assertDeleted false (Tg.quickMsg(chat = chat, text = "hello everyone"))

        let leave = Tg.quickMsg(chat = chat, text = null)
        do! assertDeleted false { leave with Message = Some { leave.Message.Value with LeftChatMember = Some (Tg.user()) } }

        do! fixture.SetBotSetting("DELETE_JOIN_MESSAGES_IN_CHATS", "[]")
        do! fixture.ReloadSettings()
        do! assertDeleted false (joinMessage chat 1)
    }

    [<Fact>]
    let ``Failed join deletion is not counted and successful retry counts once`` () = task {
        let chat = fixture.ChatsToMonitor[0]
        do! fixture.SetBotSetting("DELETE_JOIN_MESSAGES_IN_CHATS", JsonSerializer.Serialize [|chat.Id|])
        do! fixture.ReloadSettings()
        let update = joinMessage chat 1
        do! fixture.SetMethodError("deleteMessage", true)
        let! response = fixture.SendMessage update
        Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        let! recorded = deletedJoinCount update.Message.Value
        Assert.Equal(0, recorded)
        do! fixture.SetMethodError("deleteMessage", false)
        do! assertDeleted true update
        do! assertDeleted true update
        let! stored = fixture.TryGetDbMessage update.Message.Value
        Assert.True(stored.IsNone)
    }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetMethodError("deleteMessage", false)
                do! fixture.SetBotSetting("DELETE_JOIN_MESSAGES_IN_CHATS", "[]")
                do! fixture.ReloadSettings()
            })
