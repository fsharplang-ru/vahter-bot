namespace VahterBanBot

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open BotInfra
open Dapper
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Npgsql
open TL
open VahterBanBot.Types

type IJoinHistory =
    abstract Read: int64 * int[] -> Task<MessageBase[]>
    abstract Delete: int64 * int[] -> Task<unit>

module JoinHistory =
    let channelId (chat: int64) = -1000000000000L - chat

    let survivors chat (minute: DateTime) (ids: int[]) (messages: MessageBase[]) =
        let requested = Set.ofArray ids
        if messages.Length <> requested.Count || (messages |> Array.map _.ID |> Set.ofArray) <> requested then
            invalidOp "Incomplete MTProto history response"
        messages |> Array.choose (function
            | :? MessageService as m when (match m.peer_id with :? PeerChannel as p -> p.channel_id = channelId chat | _ -> false)
                                          && m.date >= minute && m.date < minute.AddMinutes(1.) ->
                match m.action with
                | :? MessageActionChatAddUser | :? MessageActionChatJoinedByLink | :? MessageActionChatJoinedByRequest -> Some m.id
                | _ -> None
            | _ -> None)

    let checkPage (history: IJoinHistory) chat minute ids allowed = task {
        let! messages = history.Read(chat, ids)
        let live = survivors chat minute ids messages
        if live.Length > 0 && allowed() then
            do! history.Delete(chat, live)
            let! after = history.Read(chat, live)
            let empty = after |> Array.choose (function :? MessageEmpty as m -> Some m.id | _ -> None) |> Set.ofArray
            if empty <> Set.ofArray live || after.Length <> live.Length then
                invalidOp "MTProto still returns a message after join deletion"
            return live.Length
        else return 0
    }

type private TelegramJoinHistory(apiId, apiHash, token: string, tg: ITelegramApi) =
    let config = function "api_id" -> apiId | "api_hash" -> apiHash | _ -> null
    let session = new MemoryStream()
    do WTelegram.Helpers.Log <- fun _ _ -> ()
    let client = new WTelegram.Client(Func<string,string>(config), session, FloodRetryThreshold = 0)
    let peers = Collections.Generic.Dictionary<int64, InputChannel>()
    let peer chat = task {
        match peers.TryGetValue chat with
        | true, channel -> return channel
        | _ ->
            let! details = tg.CallExn(Funogram.Telegram.Req.GetChat.Make(chat))
            let username = details.Username |> Option.defaultWith (fun () -> invalidOp "History check needs a resolvable supergroup username")
            let! resolved = client.Contacts_ResolveUsername username
            let id = JoinHistory.channelId chat
            match resolved.chats.TryGetValue id with
            | true, (:? Channel as ch) when details.Id = chat && ch.id = id ->
                let channel = InputChannel(channel_id=id, access_hash=ch.access_hash)
                peers.Add(chat, channel)
                return channel
            | _ -> return invalidOp "Resolved supergroup identity mismatch"
    }
    let mutable loggedIn = false
    interface IJoinHistory with
        member _.Read(chat, ids) = task {
            if not loggedIn then
                let! me = client.LoginBotIfNeeded(token)
                if not me.IsBot || string me.id <> token.Split(':')[0] then invalidOp "Unexpected Telegram bot identity"
                loggedIn <- true
            let! channel = peer chat
            let! response = client.Channels_GetMessages(channel, ids |> Array.map (fun id -> InputMessageID(id=id) :> InputMessage))
            return response.Messages
        }
        member _.Delete(chat, ids) = task {
            let! channel = peer chat
            let! _ = client.Channels_DeleteMessages(channel, ids)
            ()
        }
    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            session.Dispose()

[<CLIMutable>]
type JoinHistoryPage =
    { chat_id: int64; minute: DateTime; notices: int; next_id: int64; last_id: int; failures: int }

type JoinHistorySweep(connString: string, logger: ILogger) =
    member _.Run(history: IJoinHistory, enabledChats: unit -> int64[], ct: CancellationToken) = task {
        use conn = new NpgsqlConnection(connString)
        do! conn.OpenAsync(ct)
        // One session lock covers Telegram calls across both replicas; disposal releases it on failure.
        let! acquired = conn.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(76100462)")
        if acquired then
            use unlock = { new IAsyncDisposable with
                               member _.DisposeAsync() = ValueTask(conn.ExecuteAsync("SELECT pg_advisory_unlock(76100462)")) }
            let mutable work = true
            while work && not ct.IsCancellationRequested do
                let! pages = conn.QueryAsync<JoinHistoryPage>(
                    """SELECT chat_id, minute, notices, next_id, last_id, failures FROM join_history_check
                       WHERE notices >= 32 AND failures < 3 AND due_at <= now() AND minute > now() - interval '47 hours'
                         AND next_id <= least(2147483647, last_id + 100::bigint) AND chat_id = ANY(@chats)
                         AND NOT EXISTS (SELECT FROM join_deletion_queue WHERE flood_until > now())
                         AND NOT EXISTS (SELECT FROM join_deletion_queue q WHERE q.chat_id=join_history_check.chat_id
                                         AND q.message_id BETWEEN join_history_check.first_id AND join_history_check.last_id
                                         AND q.available_at IS NOT NULL)
                         AND NOT EXISTS (SELECT FROM join_history_cooldown WHERE until_at > now())
                       ORDER BY due_at, minute LIMIT 1""", {| chats = enabledChats() |})
                match Seq.tryHead pages with
                | None -> work <- false
                | Some page ->
                    let last = min (int64 Int32.MaxValue) (min (int64 page.last_id + 100L) (page.next_id + 99L))
                    let ids = [|int page.next_id .. int last|]
                    let key = {| chat = page.chat_id; minute = page.minute; notices = page.notices
                                 cursor = page.next_id; next = last + 1L |}
                    try
                        let! deleted = JoinHistory.checkPage history page.chat_id page.minute ids (fun () -> Array.contains page.chat_id (enabledChats()))
                        if deleted > 0 then logger.LogInformation("Verified {Count} surviving join deletions in chat {ChatId}", deleted, page.chat_id)
                        let! _ = conn.ExecuteAsync(
                            """UPDATE join_history_check SET next_id=@next, failures=0 WHERE chat_id=@chat
                               AND minute=@minute AND notices=@notices AND next_id=@cursor""", key)
                        do! Task.Delay(350, ct)
                    with
                    | :? OperationCanceledException when ct.IsCancellationRequested -> work <- false
                    | ex ->
                        let flood, delay =
                            match ex with
                            | :? RpcException as rpc when rpc.Code = 420 -> true, max 1 rpc.X
                            | _ -> false, 60
                        let! _ = conn.ExecuteAsync(
                            """UPDATE join_history_check SET due_at=now()+make_interval(secs=>@delay),
                                      failures=failures+CASE WHEN @flood THEN 0 ELSE 1 END
                               WHERE chat_id=@chat AND minute=@minute AND notices=@notices AND next_id=@cursor""",
                            {| delay=delay; flood=flood; chat=page.chat_id; minute=page.minute; notices=page.notices; cursor=page.next_id |})
                        logger.LogError("Join history check failed in {ChatId} at {Cursor} ({ErrorType}, flood wait {Flood}, attempt {Attempt})",
                                        page.chat_id, page.next_id, ex.GetType().Name, flood, page.failures + 1)
                        if flood then
                            let! _ = conn.ExecuteAsync(
                                """INSERT INTO join_history_cooldown (id, until_at) VALUES (1, now()+make_interval(secs=>@delay))
                                   ON CONFLICT (id) DO UPDATE SET until_at=greatest(join_history_cooldown.until_at, excluded.until_at)""", {| delay=delay |})
                            work <- false
    }

type JoinHistoryWorker(connString: string, options: IOptions<BotConfiguration>, tg: ITelegramApi, logger: ILogger<JoinHistoryWorker>) =
    inherit BackgroundService()
    override _.ExecuteAsync(ct) = task {
        let apiId = Environment.GetEnvironmentVariable "TELEGRAM_API_ID"
        let apiHash = Environment.GetEnvironmentVariable "TELEGRAM_API_HASH"
        if String.IsNullOrWhiteSpace apiId || String.IsNullOrWhiteSpace apiHash then
            logger.LogWarning("Join history checks require TELEGRAM_API_ID and TELEGRAM_API_HASH")
        else
            let sweep = JoinHistorySweep(connString, logger)
            let enabled () =
                let cfg = options.Value
                cfg.DeleteJoinMessagesInChats |> Array.filter (fun id -> id < -1000000000000L && cfg.ChatsToMonitor.ContainsValue id)
            use history = new TelegramJoinHistory(apiId, apiHash, options.Value.BotToken, tg)
            while not ct.IsCancellationRequested do
                try
                    do! sweep.Run(history, enabled, ct)
                with ex when not ct.IsCancellationRequested ->
                    logger.LogError("Join history worker failed ({ErrorType})", ex.GetType().Name)
                do! Task.Delay(TimeSpan.FromSeconds 30., ct)
    }
