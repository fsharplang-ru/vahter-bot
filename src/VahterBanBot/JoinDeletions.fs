namespace VahterBanBot

open System
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open BotInfra
open Dapper
open Funogram.Telegram.Types
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Npgsql
open VahterBanBot.Types

module JoinDeletions =
    let target (config: BotConfiguration) (update: Update) =
        update.EditedOrMessage
        |> Option.filter (fun message ->
            update.CallbackQuery.IsNone && update.MessageReaction.IsNone
            && (message.NewChatMembers |> Option.exists (Array.isEmpty >> not))
            && config.ChatsToMonitor.ContainsValue message.Chat.Id
            && Array.contains message.Chat.Id config.DeleteJoinMessagesInChats)

    let initialClaim = 16

    let nextPoll requested count maximum =
        if count = requested then min maximum (2 * requested), 0
        elif 2 * count >= requested then requested, 500
        else min maximum (max initialClaim (requested / 2)), 1000

[<CLIMutable>]
type JoinDeletionJob =
    { chat_id: int64
      message_id: int64
      attempt: int
      candidate_count: int }

type JoinDeletionQueue(connString: string) =
    member _.Enqueue(chatId: int64, messageId: int64, sentAt: DateTime) = task {
        use conn = new NpgsqlConnection(connString)
        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 1.)
        let! _ = conn.ExecuteAsync(CommandDefinition(
            """WITH inserted AS (
                   INSERT INTO join_deletion_queue (chat_id, message_id)
                   SELECT @chatId, @messageId WHERE NOT EXISTS
                       (SELECT 1 FROM event WHERE stream_id = @stream AND event_type IN ('JoinNotificationDeleted', 'JoinNotificationBulkDeleteAccepted'))
                   ON CONFLICT DO NOTHING RETURNING chat_id, message_id
               )
               INSERT INTO join_history_check AS h (chat_id, minute, notices, first_id, last_id, next_id, due_at)
               SELECT chat_id, date_trunc('minute', @sentAt::timestamptz), 1, message_id, message_id,
                      greatest(1, message_id - 100), now() + interval '2 minutes'
               FROM inserted WHERE chat_id < -1000000000000 AND message_id BETWEEN 1 AND 2147483647
               ON CONFLICT (chat_id, minute) DO UPDATE SET
                   notices = h.notices + 1, first_id = least(h.first_id, excluded.first_id),
                   last_id = greatest(h.last_id, excluded.last_id), next_id = least(h.next_id, excluded.next_id),
                   due_at = excluded.due_at, failures = 0""",
            {| chatId = chatId; messageId = messageId; sentAt = sentAt.ToUniversalTime(); stream = $"join-notification:{chatId}:{messageId}" |},
            cancellationToken = timeout.Token))
        ()
    }

    member _.PruneFinished() = task {
        use conn = new NpgsqlConnection(connString)
        let! _ = conn.ExecuteAsync(
            """DELETE FROM join_deletion_queue WHERE available_at IS NULL AND enqueued_at < now() - interval '2 days';
               DELETE FROM join_history_check WHERE minute < now() - interval '2 days'""")
        ()
    }

    member _.Claim(limit: int, threshold: int, ct: CancellationToken) = task {
        use conn = new NpgsqlConnection(connString)
        let! jobs = conn.QueryAsync<JoinDeletionJob>(CommandDefinition(
            """WITH head AS (
                   SELECT chat_id, single_only FROM join_deletion_queue
                   WHERE available_at <= now() AND NOT EXISTS
                       (SELECT 1 FROM join_deletion_queue WHERE flood_until > now())
                   ORDER BY available_at LIMIT 1
               ), candidates AS MATERIALIZED (
                   SELECT q.chat_id, q.message_id, q.available_at FROM join_deletion_queue q
                   JOIN head h USING (chat_id, single_only)
                   WHERE q.available_at <= now()
                   ORDER BY q.available_at, q.message_id
                   LIMIT @limit
                   FOR UPDATE OF q SKIP LOCKED
               ), claimed AS (
                   SELECT * FROM candidates ORDER BY available_at, message_id
                   LIMIT (SELECT CASE WHEN NOT (SELECT single_only FROM head) AND count(*) > @threshold
                                      THEN @limit ELSE 1 END FROM candidates)
               )
               UPDATE join_deletion_queue q SET available_at = now() + interval '3 minutes', attempt = q.attempt + 1
               FROM claimed c WHERE q.chat_id = c.chat_id AND q.message_id = c.message_id
               RETURNING q.chat_id, q.message_id, q.attempt, (SELECT count(*)::int FROM candidates) AS candidate_count""",
            {| limit = limit; threshold = threshold |}, cancellationToken = ct))
        return Seq.toArray jobs
    }

    member _.Finish(jobs: JoinDeletionJob[], makeEvent: (JoinDeletionJob -> JoinNotificationEvent) option) = task {
        use conn = new NpgsqlConnection(connString)
        let events =
            jobs |> Array.map (fun job ->
                makeEvent |> Option.map (fun f -> JsonSerializer.Serialize(f job, eventJsonOpts)) |> Option.toObj)
        let! _ = conn.ExecuteAsync(
            """WITH finished AS (
                   UPDATE join_deletion_queue q SET available_at = NULL, flood_until = NULL
                   FROM unnest(@ids, @attempts, @events) AS w(id, attempt, data)
                   WHERE q.chat_id = @chat AND q.message_id = w.id AND q.attempt = w.attempt
                       AND q.available_at IS NOT NULL RETURNING q.chat_id, q.message_id, w.data
               )
               INSERT INTO event (stream_id, stream_version, data)
               SELECT 'join-notification:' || chat_id || ':' || message_id, 1, data::jsonb
               FROM finished WHERE data IS NOT NULL ON CONFLICT (stream_id, stream_version) DO NOTHING""",
            {| chat = jobs[0].chat_id; ids = jobs |> Array.map _.message_id
               attempts = jobs |> Array.map _.attempt; events = events |})
        ()
    }

    member _.Retry(jobs: JoinDeletionJob[], delaySeconds: int, flood: bool, singleOnly: bool) = task {
        use conn = new NpgsqlConnection(connString)
        let! _ = conn.ExecuteAsync(
            """UPDATE join_deletion_queue q
               SET available_at = CASE WHEN q.enqueued_at > now() - interval '2 days'
                                       THEN now() + make_interval(secs => @delaySeconds) END,
                   flood_until = CASE WHEN @flood THEN now() + make_interval(secs => @delaySeconds) END,
                   single_only = q.single_only OR @singleOnly
               FROM unnest(@ids, @attempts) AS w(id, attempt)
               WHERE q.chat_id = @chat AND q.message_id = w.id AND q.attempt = w.attempt
                   AND q.available_at IS NOT NULL""",
            {| chat = jobs[0].chat_id; ids = jobs |> Array.map _.message_id; attempts = jobs |> Array.map _.attempt
               delaySeconds = delaySeconds; flood = flood; singleOnly = singleOnly |})
        ()
    }

type JoinDeletionWorker(queue: JoinDeletionQueue, tg: ITelegramApi, options: IOptions<BotConfiguration>,
                       logger: ILogger<JoinDeletionWorker>) =
    inherit BackgroundService()

    let retryAfter (description: string) =
        let m = Regex.Match(description, @"retry after (\d+)", RegexOptions.IgnoreCase)
        match Int32.TryParse m.Groups[1].Value with
        | true, seconds -> max 1 seconds
        | _ -> 30

    let delete (jobs: JoinDeletionJob[]) = task {
        let first = jobs[0]
        let bulk = jobs.Length > 1
        let! result =
            if bulk then tg.Call(Funogram.Telegram.Req.DeleteMessages.Make(first.chat_id, jobs |> Array.map _.message_id))
            else tg.Call(Funogram.Telegram.Req.DeleteMessage.Make(first.chat_id, first.message_id))
        match result with
        | Ok true ->
            let event (job: JoinDeletionJob) =
                let ids = {| chatId = job.chat_id; messageId = job.message_id |}
                if bulk then JoinNotificationBulkDeleteAccepted ids else JoinNotificationDeleted ids
            do! queue.Finish(jobs, Some event)
        | Error e when bulk && e.ErrorCode = 400 ->
            // A rejected batch gives no per-ID verdict. Let each ID take the individual path.
            do! queue.Retry(jobs, 0, false, true)
        | Error e when e.ErrorCode = 400 && e.Description.Contains("message to delete not found", StringComparison.OrdinalIgnoreCase) ->
            do! queue.Finish(jobs, None)
        | Error e when e.ErrorCode = 400 || e.ErrorCode = 403 ->
            logger.LogWarning("Giving up {Count} join deletions in {ChatId}, Telegram code {Code}", jobs.Length, first.chat_id, e.ErrorCode)
            do! queue.Finish(jobs, None)
        | other ->
            let flood, delay =
                match other with
                | Error e when e.ErrorCode = 429 -> true, retryAfter e.Description
                | _ -> false, 1 <<< min (jobs |> Array.maxBy _.attempt).attempt 9
            logger.LogWarning("Retrying {Count} join deletions in {ChatId} after {Seconds}s", jobs.Length, first.chat_id, delay)
            do! queue.Retry(jobs, delay, flood, false)
    }

    let poll (writer: ChannelWriter<JoinDeletionJob[]>) (ct: CancellationToken) = task {
        let mutable limit = JoinDeletions.initialClaim
        try
            while not ct.IsCancellationRequested do
                // With zero capacity, a waiting reader reserves a deletion slot before we take a database lease.
                let! _ = writer.WaitToWriteAsync(ct)
                try
                    let config = options.Value
                    let requested = min limit config.JoinDeleteMaxClaim
                    let! jobs = queue.Claim(requested, config.JoinDeleteBulkThreshold, ct)
                    let found = if jobs.Length = 0 then 0 else jobs[0].candidate_count
                    let next, delay = JoinDeletions.nextPoll requested found config.JoinDeleteMaxClaim
                    limit <- next
                    if jobs.Length > 0 then do! writer.WriteAsync(jobs)
                    if found <= jobs.Length && delay > 0 then do! Task.Delay(delay, ct)
                with
                | :? OperationCanceledException when ct.IsCancellationRequested -> ()
                | ex ->
                    logger.LogError(ex, "Join deletion claim failed")
                    do! Task.Delay(1000, ct)
        finally
            %writer.TryComplete()
    }

    let runSlot (reader: ChannelReader<JoinDeletionJob[]>) (ct: CancellationToken) = task {
        let mutable running = true
        while running do
            try
                let! jobs = reader.ReadAsync()
                try
                    do! delete jobs
                with ex ->
                    logger.LogError(ex, "Join deletion slot failed")
                    do! Task.Delay(1000, ct)
            with :? ChannelClosedException -> running <- false
    }

    override _.ExecuteAsync(ct) = task {
        let channel = Channel.CreateBounded<JoinDeletionJob[]>(BoundedChannelOptions(0, SingleWriter = true))
        let workers = Array.init 4 (fun _ -> runSlot channel.Reader ct)
        try
            do! Task.WhenAll(Array.append workers [| poll channel.Writer ct |]) :> Task
        with :? OperationCanceledException when ct.IsCancellationRequested -> ()
    }
