namespace VahterBanBot

open System
open System.Diagnostics
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
                | :? MessageActionChatAddUser | :? MessageActionChatJoinedByLink | :? MessageActionChatJoinedByRequest -> Some m
                | _ -> None
            | _ -> None)

    let checkPage (history: IJoinHistory) chat minute ids allowed = task {
        let! live = JoinHistoryTelemetry.operation "read" (fun () -> task {
            let! messages = history.Read(chat, ids)
            return survivors chat minute ids messages
        })
        JoinHistoryTelemetry.countIds "scanned" ids.Length
        JoinHistoryTelemetry.countIds "found" live.Length
        JoinHistoryTelemetry.setTag Activity.Current "history.found" live.Length
        if live.Length > 0 && allowed() then
            let targets = live |> Array.map _.id
            do! JoinHistoryTelemetry.operation "delete" (fun () -> history.Delete(chat, targets))
            JoinHistoryTelemetry.countIds "delete_accepted" targets.Length
            do! JoinHistoryTelemetry.operation "verify" (fun () -> task {
                let! after = history.Read(chat, targets)
                let empty = after |> Array.choose (function :? MessageEmpty as m -> Some m.id | _ -> None) |> Set.ofArray
                if empty <> Set.ofArray targets || after.Length <> targets.Length then
                    invalidOp "MTProto still returns a message after join deletion"
            })
            let verifiedAt = DateTime.UtcNow
            for m in live do
                JoinHistoryTelemetry.verifiedAge.Record((verifiedAt - m.date).TotalSeconds)
            JoinHistoryTelemetry.countIds "verified" targets.Length
            return targets
        else return [||]
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
            let! resolved = JoinHistoryTelemetry.operation "resolveUsername" (fun () -> client.Contacts_ResolveUsername username)
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
                let! me = JoinHistoryTelemetry.operation "login" (fun () -> client.LoginBotIfNeeded(token))
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
    { chat_id: int64; minute: DateTime; due_at: DateTime; notices: int; first_id: int; next_id: int64; last_id: int; failures: int }

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
                    """SELECT chat_id, minute, due_at, notices, first_id, next_id, last_id, failures FROM join_history_check
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
                    use span = Telemetry.botActivity.StartActivity("joinHistory.page")
                    JoinHistoryTelemetry.setTag span "chatId" page.chat_id
                    JoinHistoryTelemetry.setTag span "history.minute" (page.minute.ToString("O"))
                    JoinHistoryTelemetry.setTag span "history.cursor" page.next_id
                    JoinHistoryTelemetry.setTag span "history.ids" ids.Length
                    JoinHistoryTelemetry.setTag span "history.notices" page.notices
                    if page.next_id = max 1L (int64 page.first_id - 100L) then
                        let delay = max 0. (DateTime.UtcNow - page.due_at).TotalSeconds
                        JoinHistoryTelemetry.startDelay.Record(delay)
                        logger.LogInformation("Starting join history check in chat {ChatId} for {BucketMinute}, {SecondsPastDue:F2}s past due with {QueuedNotices} queued notices",
                                              page.chat_id, page.minute, delay, page.notices)
                    try
                        let! deleted = JoinHistory.checkPage history page.chat_id page.minute ids (fun () -> Array.contains page.chat_id (enabledChats()))
                        if deleted.Length > 0 then
                            logger.LogInformation("Verified {Count} surviving join deletions in chat {ChatId} for {BucketMinute}, message IDs {MessageIds}",
                                                  deleted.Length, page.chat_id, page.minute, String.Join(",", deleted))
                        let! advanced = conn.ExecuteAsync(
                            """UPDATE join_history_check SET next_id=@next, failures=0 WHERE chat_id=@chat
                               AND minute=@minute AND notices=@notices AND next_id=@cursor""", key)
                        let outcome = if advanced = 0 then "superseded" else "advanced"
                        JoinHistoryTelemetry.countPage outcome
                        JoinHistoryTelemetry.setTag span "history.outcome" outcome
                        if advanced > 0 && last = min (int64 Int32.MaxValue) (int64 page.last_id + 100L) then
                            JoinHistoryTelemetry.completionAge.Record((DateTime.UtcNow - page.minute.AddMinutes(1.)).TotalSeconds)
                            logger.LogInformation("Completed join history check in chat {ChatId} for {BucketMinute} with {QueuedNotices} queued notices through {LastId}",
                                                  page.chat_id, page.minute, page.notices, last)
                        if not (isNull span) then span.Stop()
                        do! Task.Delay(350, ct)
                    with
                    | :? OperationCanceledException when ct.IsCancellationRequested -> work <- false
                    | ex ->
                        let flood, delay =
                            match ex with
                            | :? RpcException as rpc when rpc.Code = 420 -> true, max 1 rpc.X
                            | _ -> false, 60
                        JoinHistoryTelemetry.markError span ex
                        let! updated = conn.ExecuteAsync(
                            """UPDATE join_history_check SET due_at=now()+make_interval(secs=>@delay),
                                      failures=failures+CASE WHEN @flood THEN 0 ELSE 1 END
                               WHERE chat_id=@chat AND minute=@minute AND notices=@notices AND next_id=@cursor""",
                            {| delay=delay; flood=flood; chat=page.chat_id; minute=page.minute; notices=page.notices; cursor=page.next_id |})
                        let outcome =
                            if flood then "flood_wait"
                            elif updated = 0 then "superseded"
                            elif page.failures + 1 >= 3 then "exhausted"
                            else "retry"
                        JoinHistoryTelemetry.countPage outcome
                        JoinHistoryTelemetry.setTag span "history.outcome" outcome
                        logger.LogError("Join history check failed in {ChatId} for {BucketMinute} at {Cursor} ({ErrorType}, {Outcome}, delay {DelaySeconds}s)",
                                        page.chat_id, page.minute, page.next_id, ex.GetType().Name, outcome, delay)
                        if flood then
                            JoinHistoryTelemetry.floodWait.Record(float delay)
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
