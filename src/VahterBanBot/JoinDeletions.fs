namespace VahterBanBot

open System
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open BotInfra
open Dapper
open Funogram.Telegram.Types
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
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

[<CLIMutable>]
type JoinDeletionJob =
    { chat_id: int64
      message_id: int64
      attempt: int
      enqueued_at: DateTime }

type JoinDeletionQueue(connString: string) =
    member _.Enqueue(chatId: int64, messageId: int64) = task {
        use conn = new NpgsqlConnection(connString)
        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 1.)
        let! _ = conn.ExecuteAsync(CommandDefinition(
            """INSERT INTO join_deletion_queue (chat_id, message_id)
               SELECT @chatId, @messageId WHERE NOT EXISTS
                   (SELECT 1 FROM event WHERE stream_id = @stream AND event_type = 'JoinNotificationDeleted')
               ON CONFLICT DO NOTHING""",
            {| chatId = chatId; messageId = messageId; stream = $"join-notification:{chatId}:{messageId}" |},
            cancellationToken = timeout.Token))
        ()
    }

    member _.PruneFinished() = task {
        use conn = new NpgsqlConnection(connString)
        let! _ = conn.ExecuteAsync(
            """DELETE FROM join_deletion_queue WHERE available_at IS NULL AND enqueued_at < now() - interval '2 days'""")
        ()
    }

    member _.Claim(limit: int) = task {
        use conn = new NpgsqlConnection(connString)
        return! conn.QueryAsync<JoinDeletionJob>(
            """WITH ready AS (
                   SELECT chat_id, message_id FROM join_deletion_queue
                   WHERE available_at <= now() AND NOT EXISTS
                       (SELECT 1 FROM join_deletion_queue WHERE flood_until > now())
                   ORDER BY available_at LIMIT @limit FOR UPDATE SKIP LOCKED
               )
               UPDATE join_deletion_queue q SET available_at = now() + interval '3 minutes', attempt = attempt + 1
               FROM ready WHERE q.chat_id = ready.chat_id AND q.message_id = ready.message_id
               RETURNING q.chat_id, q.message_id, q.attempt, q.enqueued_at""", {| limit = limit |})
    }

    member _.Finish(job: JoinDeletionJob, confirmed: bool) = task {
        use conn = new NpgsqlConnection(connString)
        let evt = JoinNotificationDeleted {| chatId = job.chat_id; messageId = job.message_id |}
        let! _ = conn.ExecuteAsync(
            """WITH finished AS (
                   UPDATE join_deletion_queue SET available_at = NULL, flood_until = NULL
                   WHERE chat_id = @chatId AND message_id = @messageId AND attempt = @attempt
                       AND available_at IS NOT NULL RETURNING 1
               )
               INSERT INTO event (stream_id, stream_version, data)
               SELECT @stream, 1, CAST(@data AS jsonb) FROM finished WHERE @confirmed
               ON CONFLICT (stream_id, stream_version) DO NOTHING""",
            {| chatId = job.chat_id; messageId = job.message_id; attempt = job.attempt
               stream = $"join-notification:{job.chat_id}:{job.message_id}"; confirmed = confirmed
               data = JsonSerializer.Serialize(evt, eventJsonOpts) |})
        ()
    }

    member _.Retry(job: JoinDeletionJob, delaySeconds: int, flood: bool) = task {
        use conn = new NpgsqlConnection(connString)
        let! _ = conn.ExecuteAsync(
            """UPDATE join_deletion_queue SET available_at = now() + make_interval(secs => @delaySeconds),
                      flood_until = CASE WHEN @flood THEN now() + make_interval(secs => @delaySeconds) END
               WHERE chat_id = @chat_id AND message_id = @message_id AND attempt = @attempt
                   AND available_at IS NOT NULL""",
            {| chat_id = job.chat_id; message_id = job.message_id; attempt = job.attempt
               delaySeconds = delaySeconds; flood = flood |})
        ()
    }

type JoinDeletionWorker(queue: JoinDeletionQueue, tg: ITelegramApi, logger: ILogger<JoinDeletionWorker>) =
    inherit BackgroundService()

    let retryAfter (description: string) =
        let m = Regex.Match(description, @"retry after (\d+)", RegexOptions.IgnoreCase)
        match Int32.TryParse m.Groups[1].Value with
        | true, seconds -> max 1 seconds
        | _ -> 30

    let delete (job: JoinDeletionJob) = task {
        let! result = tg.Call(Funogram.Telegram.Req.DeleteMessage.Make(job.chat_id, job.message_id))
        match result with
        | Ok true -> do! queue.Finish(job, true)
        | Error e when e.ErrorCode = 400 && e.Description.Contains("message to delete not found", StringComparison.OrdinalIgnoreCase) ->
            do! queue.Finish(job, false)
        | Error e when e.ErrorCode = 400 || e.ErrorCode = 403 || DateTime.UtcNow - job.enqueued_at >= TimeSpan.FromDays(2) ->
            logger.LogWarning("Giving up join deletion {ChatId}/{MessageId}, Telegram code {Code}", job.chat_id, job.message_id, e.ErrorCode)
            do! queue.Finish(job, false)
        | other ->
            let flood, delay =
                match other with
                | Error e when e.ErrorCode = 429 -> true, retryAfter e.Description
                | _ -> false, 1 <<< min job.attempt 9
            logger.LogWarning("Retrying join deletion {ChatId}/{MessageId} after {Seconds}s", job.chat_id, job.message_id, delay)
            do! queue.Retry(job, delay, flood)
    }

    override _.ExecuteAsync(ct: CancellationToken) = task {
        while not ct.IsCancellationRequested do
            try
                let! jobs = queue.Claim(4)
                let batch = jobs |> Seq.map delete |> Seq.toArray
                if batch.Length = 0 then do! Task.Delay(1000, ct)
                else do! Task.WhenAll(batch) :> Task
            with
            | :? OperationCanceledException when ct.IsCancellationRequested -> ()
            | ex ->
                logger.LogError(ex, "Join deletion worker failed")
                do! Task.Delay(1000, ct)
    }
