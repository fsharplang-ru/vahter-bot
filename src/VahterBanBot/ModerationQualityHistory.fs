namespace VahterBanBot

open System
open System.Data
open System.Diagnostics
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open System.Threading
open BotInfra
open Dapper
open Npgsql
open VahterBanBot.Types
open VahterBanBot.Utils

[<RequireQualifiedAccess>]
type QualityHistoryMode = Daily | Backfill | Rebuild of DateTime

[<CLIMutable>]
type private QualityPending = { event_id: int64; stream_id: string }

type ModerationQualityHistory(connectionString: string, clock: TimeProvider, ?logger: ILogger<ModerationQualityHistory>) =
    let logger = defaultArg logger NullLogger<ModerationQualityHistory>.Instance
    let store = EventStore(connectionString, "event", eventJsonOpts, "event_snapshot")
    let setting (conn: NpgsqlConnection) name fallback = task {
        let! value = conn.QuerySingleOrDefaultAsync<string>("SELECT value FROM bot_setting WHERE key = @name", {| name = name |})
        return match Int32.TryParse value with true, n when n > 0 -> n | _ -> fallback
    }

    let rebuild (conn: NpgsqlConnection) (day: DateTime) (ct: CancellationToken) = task {
        use! tx = conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
        let! candidates = conn.QueryAsync<string>(CommandDefinition("""
            SELECT DISTINCT stream_id FROM event
            WHERE event_type = 'MlScoredMessage' AND created_at >= @day AND created_at < @finish
            """, {| day = day; finish = day.AddDays 1.0 |}, tx, cancellationToken = ct))
        let counts = Collections.Generic.Dictionary<string * string * string option * string, int64>()
        let increment cohort system model code =
            let key = cohort, system, model, code
            let found, count = counts.TryGetValue key
            counts[key] <- (if found then count else 0L) + 1L
        for batch in candidates |> Seq.chunkBySize 250 do
            ct.ThrowIfCancellationRequested()
            let suffixes = batch |> Array.map (fun s -> s.Substring("detection:".Length))
            let ids = [ for s in suffixes do for prefix in ["message:"; "moderation:"; "detection:"] do yield prefix + s ]
            let! rows = store.ReadRawEventsForStreams(conn, tx, ids)
            let users =
                [ for s in suffixes do
                    for r in rows["message:" + s] do
                        match store.Deserialize<MessageEvent> r with
                        | MessageReceived e -> yield $"user:{e.userId}"
                        | _ -> () ] |> List.distinct
            let! bans = conn.QueryAsync<RawEvent>(CommandDefinition("""
                SELECT id, stream_id, stream_version, event_type, data::text, created_at FROM event
                WHERE stream_id = ANY(@users) AND event_type IN ('UserBanned', 'UserUnbanned')
                """, {| users = List.toArray users |}, tx, cancellationToken = ct))
            let unbans = bans |> Seq.groupBy (fun r -> r.stream_id) |> Seq.collect (fun (_, rs) -> ModerationQuality.unbans store (Seq.toList rs)) |> Seq.toList
            for s in suffixes do
                let parts = s.Split ':'
                let corrections = unbans |> List.filter (fun c -> c.ChatId = Int64.Parse parts[0] && c.MessageId = Int64.Parse parts[1])
                match ModerationQuality.evaluate store rows["message:" + s] rows["moderation:" + s] rows["detection:" + s] corrections with
                | Some evaluation when evaluation.EvaluatedAt.Date = day ->
                    for c in evaluation.Contributions do increment c.Cohort c.System c.LlmModel c.Outcome.Code
                | _ -> ()
        for system in ["pipeline"; "ml"] do
            let key = "all_scored", system, None, "tp"
            if not (counts.ContainsKey key) then counts[key] <- 0L
        let! _ = conn.ExecuteAsync(CommandDefinition("DELETE FROM moderation_quality_daily WHERE day = @day", {| day = day |}, tx, cancellationToken = ct))
        for ((cohort, system, model), values) in counts |> Seq.groupBy (fun kv -> let c, s, m, _ = kv.Key in c, s, m) do
            let value code = values |> Seq.sumBy (fun kv -> let _, _, _, c = kv.Key in if c = code then kv.Value else 0L)
            let! _ = conn.ExecuteAsync(CommandDefinition("""
                INSERT INTO moderation_quality_daily (day, cohort, system, llm_model, tp, tn, fp, fn, abstained, unresolved, excluded)
                VALUES (@day, @cohort, @system, @model, @tp, @tn, @fp, @fn, @abstained, @unresolved, @excluded)
                """, {| day = day; cohort = cohort; system = system; model = Option.toObj model;
                     tp = value "tp"; tn = value "tn"; fp = value "fp"; fn = value "fn";
                     abstained = value "abstained"; unresolved = value "unresolved"; excluded = value "excluded" |}, tx, cancellationToken = ct))
            ()
        let! _ = conn.ExecuteAsync(CommandDefinition("DELETE FROM moderation_quality_dirty_day WHERE day = @day", {| day = day |}, tx, cancellationToken = ct))
        do! tx.CommitAsync ct
    }

    let collectDirtyDays (conn: NpgsqlConnection) (ct: CancellationToken) = task {
        use! tx = conn.BeginTransactionAsync(ct)
        let! pending = conn.QueryAsync<QualityPending>(CommandDefinition(
            "SELECT event_id, stream_id FROM moderation_quality_pending", transaction = tx, cancellationToken = ct))
        let pending = Seq.toArray pending
        let days = Collections.Generic.HashSet<DateTime>()
        for batch in Array.chunkBySize 500 pending do
            let eventIds = batch |> Array.map (fun e -> e.event_id) |> Set.ofArray
            let users = batch |> Array.filter (fun e -> e.stream_id.StartsWith("user:")) |> Array.map (fun e -> e.stream_id) |> Array.distinct
            let! bans = conn.QueryAsync<RawEvent>(CommandDefinition("""
                SELECT id, stream_id, stream_version, event_type, data::text, created_at FROM event
                WHERE stream_id = ANY(@users) AND event_type IN ('UserBanned', 'UserUnbanned')
                """, {| users = users |}, tx, cancellationToken = ct))
            let targets =
                [| for e in batch do
                       if e.stream_id.StartsWith("detection:") then yield e.stream_id
                   for _, rows in bans |> Seq.groupBy (fun r -> r.stream_id) do
                       for unban in ModerationQuality.unbans store (Seq.toList rows) do
                           if eventIds.Contains unban.Id then yield $"detection:{unban.ChatId}:{unban.MessageId}" |]
                |> Array.distinct
            let! affected = conn.QueryAsync<DateTime>(CommandDefinition("""
                SELECT DISTINCT date_trunc('day', first.created_at, 'UTC') FROM unnest(@targets::text[]) AS target
                CROSS JOIN LATERAL (
                    SELECT created_at FROM event WHERE stream_id = target AND event_type = 'MlScoredMessage'
                    ORDER BY created_at, id LIMIT 1
                ) first
                """, {| targets = targets |}, tx, cancellationToken = ct))
            for day in affected do %days.Add day
        if days.Count > 0 then
            let! _ = conn.ExecuteAsync(CommandDefinition("""
                INSERT INTO moderation_quality_dirty_day (day)
                SELECT (d AT TIME ZONE 'UTC')::date FROM unnest(@days::timestamptz[]) AS d
                ON CONFLICT DO NOTHING
                """, {| days = Seq.toArray days |}, tx, cancellationToken = ct))
            ()
        if pending.Length > 0 then
            let! _ = conn.ExecuteAsync(CommandDefinition(
                "DELETE FROM moderation_quality_pending WHERE event_id = ANY(@ids)",
                {| ids = pending |> Array.map (fun e -> e.event_id) |}, tx, cancellationToken = ct))
            ()
        do! tx.CommitAsync ct
    }

    member _.Run(mode: QualityHistoryMode, ct: CancellationToken) = task {
        let elapsed = Stopwatch.StartNew()
        let connection = NpgsqlConnectionStringBuilder(connectionString)
        connection.Pooling <- false
        use conn = new NpgsqlConnection(connection.ConnectionString)
        do! conn.OpenAsync ct
        let today = clock.GetUtcNow().UtcDateTime.Date
        let cutoff = today.AddDays -14.0
        match mode with
        | QualityHistoryMode.Rebuild day when day.Kind <> DateTimeKind.Utc || day <> day.Date || day > today ->
            invalidArg (nameof mode) "Day must be a UTC date no later than today"
        | _ -> ()
        let! acquired = conn.QuerySingleAsync<bool>("SELECT pg_try_advisory_lock(734829105)")
        if not acquired then invalidOp "A quality history job is already running"
        let! days = task {
            match mode with
            | QualityHistoryMode.Rebuild day -> return [day]
            | QualityHistoryMode.Daily ->
                do! collectDirtyDays conn ct
                let! dirty = conn.QueryAsync<DateTime>(CommandDefinition(
                    "SELECT day::timestamp AT TIME ZONE 'UTC' FROM moderation_quality_dirty_day WHERE day <= @today",
                    {| today = today |}, cancellationToken = ct))
                return [ yield! dirty; for offset in -14..0 do yield today.AddDays(float offset) ] |> List.distinct |> List.sort
            | QualityHistoryMode.Backfill ->
                let! limit = setting conn "QUALITY_BACKFILL_DAYS" 7
                let! result = conn.QueryAsync<DateTime>(CommandDefinition("""
                    SELECT d AT TIME ZONE 'UTC' FROM generate_series(
                        (SELECT min(created_at) AT TIME ZONE 'UTC' FROM event WHERE event_type = 'MlScoredMessage')::date::timestamp,
                        (@cutoff AT TIME ZONE 'UTC') - interval '1 day', interval '1 day') d
                    WHERE NOT EXISTS (SELECT 1 FROM moderation_quality_daily q WHERE q.day = d::date AND q.cohort = 'all_scored' AND q.system = 'pipeline')
                    ORDER BY d LIMIT @limit
                    """, {| cutoff = cutoff; limit = min limit 31 |}, cancellationToken = ct))
                return Seq.toList result
        }
        for day in days do do! rebuild conn day ct
        let name = match mode with QualityHistoryMode.Daily -> "daily" | QualityHistoryMode.Backfill -> "backfill" | QualityHistoryMode.Rebuild _ -> "rebuild"
        logger.LogInformation("Moderation quality {Mode} completed {CompletedDays} days in {DurationSeconds} seconds", name, days.Length, elapsed.Elapsed.TotalSeconds)
        if mode = QualityHistoryMode.Daily && elapsed.Elapsed.TotalSeconds > 30.0 then
            logger.LogWarning("Moderation quality daily refresh exceeded 30-second budget: {DurationSeconds} seconds", elapsed.Elapsed.TotalSeconds)
        return days.Length
    }
