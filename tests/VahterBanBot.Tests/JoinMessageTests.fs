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

    let waitFor predicate = task {
        let deadline = DateTime.UtcNow.AddSeconds 20
        let mutable reached = false
        while not reached && DateTime.UtcNow < deadline do
            let! seen = predicate()
            reached <- seen
            if not reached then do! Task.Delay 100
        Assert.True(reached, "Timed out waiting for join deletion")
    }

    let matchingCalls (message: Funogram.Telegram.Types.Message) = task {
        let! calls = fixture.GetFakeCalls "deleteMessage"
        return calls |> Array.filter (fun call ->
            use body = JsonDocument.Parse call.Body
            body.RootElement.GetProperty("chat_id").GetInt64() = message.Chat.Id
            && body.RootElement.GetProperty("message_id").GetInt64() = message.MessageId)
    }

    let waitForDeletion message =
        waitFor (fun () -> task {
            let! recorded = deletedJoinCount message
            return recorded = 1
        })

    let assertDeleted expected (update: Funogram.Telegram.Types.Update) = task {
        use! response = fixture.SendMessage update
        Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        let message = update.Message.Value
        if expected then
            do! waitForDeletion message
        else
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            let! queued = conn.ExecuteScalarAsync<bool>(
                """SELECT EXISTS (SELECT FROM join_deletion_queue WHERE chat_id = @chatId AND message_id = @messageId)""",
                {| chatId = message.Chat.Id; messageId = message.MessageId |})
            Assert.False(queued)
        let! calls = matchingCalls message
        Assert.Equal((if expected then 1 else 0), calls.Length)
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
    let ``Rate-limited deletion survives restart and pauses fresh jobs`` () = task {
        let chat = fixture.ChatsToMonitor[0]
        do! fixture.SetBotSetting("DELETE_JOIN_MESSAGES_IN_CHATS", JsonSerializer.Serialize [|chat.Id|])
        do! fixture.ReloadSettings()
        let update = joinMessage chat 1
        do! fixture.SetMethodError("deleteMessage", true, errorCode = 429, description = "Too Many Requests: retry after 8")
        let! response = fixture.SendMessage update
        Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        let message = update.Message.Value
        do! waitFor (fun () -> task {
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            return! conn.ExecuteScalarAsync<bool>(
                """SELECT EXISTS (SELECT FROM join_deletion_queue WHERE chat_id = @chatId AND message_id = @messageId
                                  AND attempt >= 1 AND flood_until > now())""",
                {| chatId = chat.Id; messageId = message.MessageId |})
        })
        let! recorded = deletedJoinCount message
        Assert.Equal(0, recorded)
        let another = joinMessage chat 1
        use! accepted = fixture.SendMessage another
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode)
        do! Task.Delay 1500
        let! held = matchingCalls another.Message.Value
        Assert.Empty(held)
        do! fixture.RestartBotApp()
        do! fixture.SetMethodError("deleteMessage", false)
        do! waitForDeletion another.Message.Value
        do! waitForDeletion message
        let! stored = fixture.TryGetDbMessage update.Message.Value
        Assert.True(stored.IsNone)
    }

    [<Theory>]
    [<InlineData("Bad Request: message to delete not found")>]
    [<InlineData("Bad Request: message can't be deleted")>]
    let ``Terminal Telegram replies retire the job without recording a confirmed deletion`` description = task {
        let chat = fixture.ChatsToMonitor[0]
        do! fixture.SetBotSetting("DELETE_JOIN_MESSAGES_IN_CHATS", JsonSerializer.Serialize [|chat.Id|])
        do! fixture.ReloadSettings()
        let update = joinMessage chat 1
        let message = update.Message.Value
        do! fixture.SetMethodError("deleteMessage", true, description = description)
        use! accepted = fixture.SendMessage update
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode)
        do! waitFor (fun () -> task {
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            return! conn.ExecuteScalarAsync<bool>(
                """SELECT EXISTS (SELECT FROM join_deletion_queue WHERE chat_id = @chatId AND message_id = @messageId
                                  AND available_at IS NULL)""", {| chatId = chat.Id; messageId = message.MessageId |})
        })
        use! replay = fixture.SendMessage update
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode)
        let! recorded = deletedJoinCount message
        let! calls = matchingCalls message
        Assert.Equal(0, recorded)
        Assert.Equal(1, calls.Length)
    }

    [<Fact>]
    let ``A blocked join queue acknowledges the notice and the next chat message is processed`` () = task {
        let chat = fixture.ChatsToMonitor[0]
        do! fixture.SetBotSetting("DELETE_JOIN_MESSAGES_IN_CHATS", JsonSerializer.Serialize [|chat.Id|])
        do! fixture.ReloadSettings()
        let join = joinMessage chat 1
        use admin = new NpgsqlConnection(fixture.AdminDbConnectionString)
        do! admin.OpenAsync()
        use! tx = admin.BeginTransactionAsync()
        let! _ = admin.ExecuteAsync("LOCK TABLE join_deletion_queue IN SHARE MODE", transaction = tx)
        use! response = (fixture.SendMessage join).WaitAsync(TimeSpan.FromSeconds 5.)
        Assert.Equal(HttpStatusCode.OK, response.StatusCode)

        let next = Tg.quickMsg(chat = chat, text = "hello everyone")
        use! nextResponse = fixture.SendMessage next
        Assert.Equal(HttpStatusCode.OK, nextResponse.StatusCode)
        let! stored = fixture.TryGetDbMessage next.Message.Value
        Assert.True(stored.IsSome)
        do! tx.CommitAsync()
        let! queued = admin.ExecuteScalarAsync<bool>(
            """SELECT EXISTS (SELECT FROM join_deletion_queue WHERE chat_id = @chatId AND message_id = @messageId)""",
            {| chatId = chat.Id; messageId = join.Message.Value.MessageId |})
        Assert.False(queued)
    }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetMethodError("deleteMessage", false)
                do! fixture.SetBotSetting("DELETE_JOIN_MESSAGES_IN_CHATS", "[]")
                do! fixture.ReloadSettings()
            })
