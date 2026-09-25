namespace BotInfra

open System
open System.Collections.Generic
open System.Diagnostics
open System.Diagnostics.Metrics
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Dapper
open Npgsql

/// OpenTelemetry primitives for the event store. Self-contained in BotInfra so any bot gets
/// the same spans/metrics; the source/meter names are registered in `Observability.fs`.
/// A `eventStore.load` span is emitted per stream load tagged `source = db | cache`, so a
/// cache hit is visible in Tempo as a span with NO child `postgresql` span.
module internal EventStoreTelemetry =
    let activitySource = new ActivitySource("BotInfra.EventStore")
    let private meter = new Meter("BotInfra.EventStore")
    let private streamLoadsCounter =
        meter.CreateCounter<int64>(
            "eventstore_stream_loads_total", "loads",
            "Event-stream loads into aggregate state, tagged by source (db|cache)")
    let private cacheMutationsCounter =
        meter.CreateCounter<int64>(
            "eventstore_stream_cache_mutations_total", "mutations",
            "Request-scoped stream-cache mutations, tagged by action (appended|evicted)")
    let private snapshotOpsCounter =
        meter.CreateCounter<int64>(
            "eventstore_snapshot_ops_total", "ops",
            "Aggregate-snapshot outcomes, tagged by state_type and op (hit|miss|written|skipped|discarded_ahead|discarded_unreadable|write_failed|delete_failed)")

    /// Records a load and tags the (possibly null) load span with its provenance.
    let recordLoad (activity: Activity) (source: string) (streamId: string) (version: int) (eventCount: int) =
        if not (isNull activity) then
            activity.SetTag("stream_id", streamId)
                    .SetTag("source", source)
                    .SetTag("cache.hit", box (source = "cache"))
                    .SetTag("stream_version", version)
                    .SetTag("event_count", eventCount)
            |> ignore
        streamLoadsCounter.Add(1L, KeyValuePair("source", box source))

    /// Records a cache mutation (append/evict) on a metric and the enclosing span, if any.
    let recordMutation (action: string) (streamId: string) =
        cacheMutationsCounter.Add(1L, KeyValuePair("action", box action))
        match Activity.Current with
        | null -> ()
        | a -> a.SetTag("eventstore.cache.action", action) |> ignore

    /// Records a snapshot outcome on a metric and as a tag on the (possibly null) span.
    let recordSnapshot (activity: Activity) (stateType: string) (op: string) =
        snapshotOpsCounter.Add(1L, KeyValuePair("state_type", box stateType), KeyValuePair("op", box op))
        if not (isNull activity) then
            activity.AddEvent(ActivityEvent($"snapshot.{op}")) |> ignore

/// Raw row materialized from the per-bot event table.
/// `data` is JSONB read back as TEXT so Dapper can map it as a plain string.
[<CLIMutable>]
type RawEvent =
    { id:             int64
      stream_id:      string
      stream_version: int
      event_type:     string
      data:           string
      created_at:     DateTime }

/// Sentinel returned by TryAppend when another writer already inserted at the
/// expected version. The caller's job is to re-read state and retry.
type ConcurrencyConflict = ConcurrencyConflict

/// A snapshot-aware load row: the usable snapshot (if any) first, then the tail events.
/// `head_version` (snapshot row only) is the log's max version, to detect a snapshot ahead of it.
[<CLIMutable>]
type internal SnapshotLoadRow =
    { is_snapshot:    bool
      stream_version: int
      data:           string
      head_version:   Nullable<int> }

/// Snapshot-loaded aggregate state; `SnapshotVersion` is the version persisted in the snapshot table.
type internal LoadedState =
    { StateType:       string
      State:           obj
      Version:         int
      SnapshotVersion: int }

/// Request-scoped identity map (raw streams + snapshot-loaded states). Access is serialized; a put
/// only lands if its stream wasn't invalidated since the caller observed `Generation`.
[<AllowNullLiteral>]
type internal RequestCache() =
    let gate = obj ()
    let raws = Dictionary<string, RawEvent list * int>()
    let states = Dictionary<string, LoadedState>()
    let generations = Dictionary<string, int64>()
    let mutable disposed = false
    let generation streamId =
        match generations.TryGetValue streamId with
        | true, g -> g
        | _ -> 0L
    let tryFind (d: Dictionary<string, 'T>) streamId =
        match d.TryGetValue streamId with
        | true, v when not disposed -> Some v
        | _ -> None

    member _.Generation(streamId: string) = lock gate (fun () -> generation streamId)
    member _.TryGetRaws(streamId: string) = lock gate (fun () -> tryFind raws streamId)
    member _.TryGetState(streamId: string) = lock gate (fun () -> tryFind states streamId)
    member _.PutRaws(streamId: string, observed: int64, entry) =
        lock gate (fun () -> if not disposed && generation streamId = observed then raws[streamId] <- entry)
    member _.PutState(streamId: string, observed: int64, entry) =
        lock gate (fun () -> if not disposed && generation streamId = observed then states[streamId] <- entry)
    /// Drops both views of the stream; returns the new generation and whether anything was cached.
    member _.Invalidate(streamId: string) : int64 * bool =
        lock gate (fun () ->
            let next = generation streamId + 1L
            generations[streamId] <- next
            let removedRaws = raws.Remove streamId
            let removedState = states.Remove streamId
            next, (removedRaws || removedState))
    /// Work that outlives the scope (fire-and-forget) keeps the reference; it must stop caching.
    member _.Dispose() =
        lock gate (fun () ->
            disposed <- true
            raws.Clear()
            states.Clear())

/// Append-only event store wrapper. One instance per (connection-string, event-table)
/// pair. Each bot owns its own event table — this wrapper does not attempt to merge them.
///
/// `tableName` is interpolated into SQL because Postgres has no parameterized identifier
/// syntax. It must come from trusted bot config, never user input. The constructor
/// validates the shape against `^[a-z_][a-z0-9_]{0,62}$`.
///
/// `jsonOptions` is the caller's choice of JSON serializer config. The serialized form
/// must produce a `Case` discriminator field at the top of every event payload — the
/// schema's GENERATED `event_type` column reads it as `data->>'Case'`. FSharp.SystemTextJson
/// with `WithUnionInternalTag()` is the canonical pick.
///
/// The event table must follow the schema described in `EVENTSTORE.md`.
/// `snapshotTableName` (optional, same schema doc) enables the `SnapshotPolicy` overloads.
type EventStore(connString: string, tableName: string, jsonOptions: JsonSerializerOptions, ?snapshotTableName: string) =
    static let identifierPattern = @"^[a-z_][a-z0-9_]{0,62}$"
    do
        if isNull connString then nullArg (nameof connString)
        if isNull tableName then nullArg (nameof tableName)
        if isNull jsonOptions then nullArg (nameof jsonOptions)
        if not (Regex.IsMatch(tableName, identifierPattern)) then
            invalidArg (nameof tableName) $"invalid event table name: %s{tableName}"
        match snapshotTableName with
        | Some snap when not (Regex.IsMatch(snap, identifierPattern)) ->
            invalidArg (nameof snapshotTableName) $"invalid snapshot table name: %s{snap}"
        | _ -> ()

    let selectAllSql =
        $"""
SELECT id::BIGINT AS id, stream_id, stream_version, event_type, data::TEXT AS data, created_at
FROM {tableName}
WHERE stream_id = @streamId
ORDER BY stream_version
"""

    let insertSql =
        $"""
INSERT INTO {tableName}(stream_id, stream_version, data)
VALUES (@stream_id, @stream_version, @data::JSONB)
ON CONFLICT (stream_id, stream_version) DO NOTHING
RETURNING id
"""

    let maxVersionSql =
        $"SELECT MAX(stream_version) FROM {tableName} WHERE stream_id = @streamId"

    let snapshotTable = defaultArg snapshotTableName ""

    let validatePolicy (policy: SnapshotPolicy) =
        if snapshotTableName.IsNone then
            invalidOp "SnapshotPolicy requires an EventStore constructed with snapshotTableName"
        if String.IsNullOrWhiteSpace policy.StateType then
            invalidArg (nameof policy) "SnapshotPolicy.StateType must be set"
        if policy.SnapshotEvery < 1 then
            invalidArg (nameof policy) "SnapshotPolicy.SnapshotEvery must be >= 1"

    // One statement, so the snapshot and the tail boundary come from the same MVCC snapshot —
    // a concurrent snapshot rewrite can never make the tail skip events.
    let snapshotLoadSql =
        $"""
WITH snap AS (
    SELECT stream_version, state
    FROM {snapshotTable}
    WHERE stream_id = @streamId AND state_type = @stateType AND schema_version = @schemaVersion
)
SELECT TRUE AS is_snapshot, s.stream_version, s.state::TEXT AS data,
       (SELECT MAX(stream_version) FROM {tableName} WHERE stream_id = @streamId) AS head_version
FROM snap s
UNION ALL
SELECT FALSE, e.stream_version, e.data::TEXT, NULL
FROM {tableName} e
WHERE e.stream_id = @streamId
  AND e.stream_version > COALESCE((SELECT stream_version FROM snap), 0)
ORDER BY is_snapshot DESC, stream_version
"""

    // Never moves a snapshot backwards within a schema version; a different schema version
    // (newer code, or a rollback) always overwrites.
    let snapshotWriteSql =
        $"""
INSERT INTO {snapshotTable} (stream_id, state_type, schema_version, stream_version, state)
VALUES (@streamId, @stateType, @schemaVersion, @streamVersion, @state::JSONB)
ON CONFLICT (stream_id, state_type) DO UPDATE
   SET schema_version = EXCLUDED.schema_version,
       stream_version = EXCLUDED.stream_version,
       state          = EXCLUDED.state,
       updated_at     = now()
 WHERE {snapshotTable}.schema_version <> EXCLUDED.schema_version
    OR {snapshotTable}.stream_version < EXCLUDED.stream_version
"""

    // Discard path only: the stored row is known-bad (ahead of its log or unreadable), so the guard
    // that keeps snapshots monotonic must not protect it.
    let snapshotForceWriteSql =
        $"""
INSERT INTO {snapshotTable} (stream_id, state_type, schema_version, stream_version, state)
VALUES (@streamId, @stateType, @schemaVersion, @streamVersion, @state::JSONB)
ON CONFLICT (stream_id, state_type) DO UPDATE
   SET schema_version = EXCLUDED.schema_version,
       stream_version = EXCLUDED.stream_version,
       state          = EXCLUDED.state,
       updated_at     = now()
"""

    let snapshotDeleteSql =
        $"""
DELETE FROM {snapshotTable}
WHERE stream_id = @streamId AND state_type = @stateType
  AND schema_version = @schemaVersion AND stream_version = @streamVersion
"""

    // Request-scoped identity map (unit of work): while a scope is active, repeated loads of the
    // same stream within one handle are served from memory instead of re-querying Postgres. The
    // AsyncLocal value is null outside a scope (no caching). Only the `readStream` path is cached;
    // the in-TX projection read (`ReadRawEventsForStream`) is deliberately left uncached so
    // projections always see freshly-inserted rows with their real DB-assigned id/created_at.
    // NOTE: cached raws are only ever folded by `data` + `stream_version` (id/created_at/event_type
    // are never read off the cache — verified for all readStream consumers), so appended events are
    // synthesized in-memory below without a re-read.
    let scopedCache = AsyncLocal<RequestCache>()

    /// The current scope's generation for `streamId`, captured before a DB read (None outside a scope).
    let observeGeneration (streamId: string) : int64 option =
        match scopedCache.Value with
        | null -> None
        | c -> Some (c.Generation streamId)

    let cacheTryGet (streamId: string) : (RawEvent list * int) option =
        match scopedCache.Value with
        | null -> None
        | c -> c.TryGetRaws streamId

    let cachePut (streamId: string) (observed: int64 option) (entry: RawEvent list * int) =
        match scopedCache.Value, observed with
        | null, _ | _, None -> ()
        | c, Some g -> c.PutRaws(streamId, g, entry)

    // Snapshot-loaded states live in the same scope; every committed append drops both views.
    let stateCacheTryGet (streamId: string) (stateType: string) : LoadedState option =
        match scopedCache.Value with
        | null -> None
        | c -> c.TryGetState streamId |> Option.filter (fun e -> e.StateType = stateType)

    let stateCachePut (streamId: string) (observed: int64 option) (entry: LoadedState) =
        match scopedCache.Value, observed with
        | null, _ | _, None -> ()
        | c, Some g -> c.PutState(streamId, g, entry)

    /// Drops every cached view of a stream; returns the generation the caller may put under.
    let invalidateStream (streamId: string) : int64 option =
        match scopedCache.Value with
        | null -> None
        | c -> Some (fst (c.Invalidate streamId))

    /// Same as `invalidateStream` after a lost version race, counted as an eviction.
    let cacheEvict (streamId: string) =
        match scopedCache.Value with
        | null -> ()
        | c -> if snd (c.Invalidate streamId) then EventStoreTelemetry.recordMutation "evicted" streamId

    /// Reflects an append into the cache (if a scope is active) by synthesizing the new rows in
    /// memory, so a subsequent load in the same handle is free and reflects our own write.
    /// Synthesized rows carry meaningful `data` + `stream_version` only (see note above).
    let cacheAppend (streamId: string) (observed: int64 option) (priorRaws: RawEvent list) (baseVersion: int) (newEvents: 'TEvent list) =
        if observed.IsSome then
            let synthesized =
                newEvents
                |> List.mapi (fun i e ->
                    { id = 0L
                      stream_id = streamId
                      stream_version = baseVersion + i + 1
                      event_type = ""
                      data = JsonSerializer.Serialize<'TEvent>(e, jsonOptions)
                      created_at = Unchecked.defaultof<DateTime> })
            let newVersion = baseVersion + List.length newEvents
            cachePut streamId observed (priorRaws @ synthesized, newVersion)
            EventStoreTelemetry.recordMutation "appended" streamId

    /// Upserts a snapshot. A failure is recorded (metric + span error) and swallowed: snapshots
    /// are a cache, so a failed write must never fail the load or the committed append.
    let tryWriteSnapshot (conn: NpgsqlConnection) (activity: Activity) (policy: SnapshotPolicy) (force: bool)
                         (streamId: string) (version: int) (state: 'State) : Task<bool> =
        task {
            try
                let json = JsonSerializer.Serialize<'State>(state, jsonOptions)
                let! affected =
                    conn.ExecuteAsync((if force then snapshotForceWriteSql else snapshotWriteSql),
                        {| streamId = streamId; stateType = policy.StateType; schemaVersion = policy.SchemaVersion
                           streamVersion = version; state = json |})
                // 0 rows = the monotonic guard kept a newer snapshot another writer stored first.
                let op = if affected > 0 then "written" else "skipped"
                EventStoreTelemetry.recordSnapshot activity policy.StateType op
                return affected > 0
            with ex ->
                EventStoreTelemetry.recordSnapshot activity policy.StateType "write_failed"
                if not (isNull activity) then
                    %activity.AddException(ex).SetStatus(ActivityStatusCode.Error, "snapshot write failed")
                return false
        }

    /// Removes a known-bad snapshot of an empty stream; failures are recorded and swallowed.
    let tryDeleteSnapshot (conn: NpgsqlConnection) (activity: Activity) (policy: SnapshotPolicy)
                          (streamId: string) (badVersion: int) : Task =
        task {
            try
                let! _ =
                    conn.ExecuteAsync(snapshotDeleteSql,
                        {| streamId = streamId; stateType = policy.StateType
                           schemaVersion = policy.SchemaVersion; streamVersion = badVersion |})
                ()
            with ex ->
                EventStoreTelemetry.recordSnapshot activity policy.StateType "delete_failed"
                if not (isNull activity) then
                    %activity.AddException(ex).SetStatus(ActivityStatusCode.Error, "snapshot delete failed")
        }

    /// Loads aggregate state as snapshot + tail. A snapshot that is ahead of its log or can't be
    /// deserialized is ignored, the stream replayed from scratch, and the row overwritten.
    let loadSnapshotted (fold: 'State -> 'TEvent -> 'State) (zero: 'State) (policy: SnapshotPolicy)
                        (streamId: string) : Task<LoadedState> =
        task {
            validatePolicy policy
            match stateCacheTryGet streamId policy.StateType with
            | Some entry ->
                use activity = EventStoreTelemetry.activitySource.StartActivity("eventStore.load")
                EventStoreTelemetry.recordLoad activity "cache" streamId entry.Version 0
                return entry
            | None ->
                use activity = EventStoreTelemetry.activitySource.StartActivity("eventStore.load")
                let observed = observeGeneration streamId
                use conn = new NpgsqlConnection(connString)
                let keyArgs =
                    {| streamId = streamId; stateType = policy.StateType; schemaVersion = policy.SchemaVersion |}
                let! rows = conn.QueryAsync<SnapshotLoadRow>(snapshotLoadSql, keyArgs)
                let rows = List.ofSeq rows
                let snapshotRow, tailRows =
                    match rows with
                    | r :: rest when r.is_snapshot -> Some r, rest
                    | _ -> None, rows
                let tail = tailRows |> List.map (fun r -> r.stream_version, r.data)
                let fromSnapshot =
                    match snapshotRow with
                    | None -> Ok None
                    | Some r when not r.head_version.HasValue || r.head_version.Value < r.stream_version ->
                        Error ("discarded_ahead", r.stream_version)
                    | Some r ->
                        try
                            match box (JsonSerializer.Deserialize<'State>(r.data, jsonOptions)) with
                            | null -> Error ("discarded_unreadable", r.stream_version)
                            | s -> Ok (Some (unbox<'State> s, r.stream_version))
                        with ex ->
                            if not (isNull activity) then %activity.AddException ex
                            Error ("discarded_unreadable", r.stream_version)
                let! (baseState, baseVersion, events) =
                    task {
                        match fromSnapshot with
                        | Ok (Some (s, v)) ->
                            EventStoreTelemetry.recordSnapshot activity policy.StateType "hit"
                            return s, v, tail
                        | Ok None ->
                            EventStoreTelemetry.recordSnapshot activity policy.StateType "miss"
                            return zero, 0, tail
                        | Error (op, _) ->
                            EventStoreTelemetry.recordSnapshot activity policy.StateType op
                            let! raws = conn.QueryAsync<RawEvent>(selectAllSql, {| streamId = streamId |})
                            return zero, 0, (raws |> Seq.map (fun r -> r.stream_version, r.data) |> List.ofSeq)
                    }
                let state =
                    events
                    |> List.fold (fun s (_, data) -> fold s (JsonSerializer.Deserialize<'TEvent>(data, jsonOptions))) baseState
                let version =
                    match List.tryLast events with
                    | Some (v, _) -> v
                    | None -> baseVersion
                let! snapshotVersion =
                    match fromSnapshot with
                    | Error (_, badVersion) when version = 0 ->
                        task {
                            do! tryDeleteSnapshot conn activity policy streamId badVersion
                            return 0
                        }
                    | Error _ ->
                        task {
                            let! written = tryWriteSnapshot conn activity policy true streamId version state
                            return if written then version else 0
                        }
                    | Ok _ when version - baseVersion >= policy.SnapshotEvery ->
                        task {
                            let! written = tryWriteSnapshot conn activity policy false streamId version state
                            return if written then version else baseVersion
                        }
                    | Ok _ -> Task.FromResult baseVersion
                EventStoreTelemetry.recordLoad activity "db" streamId version (List.length events)
                if not (isNull activity) then %activity.SetTag("snapshot_version", baseVersion)
                let entry =
                    { StateType = policy.StateType; State = box state
                      Version = version; SnapshotVersion = snapshotVersion }
                stateCachePut streamId observed entry
                return entry
        }

    let readStream (streamId: string) : Task<RawEvent list * int> =
        task {
            match cacheTryGet streamId with
            | Some (events, version) ->
                use activity = EventStoreTelemetry.activitySource.StartActivity("eventStore.load")
                EventStoreTelemetry.recordLoad activity "cache" streamId version (List.length events)
                return events, version
            | None ->
                use activity = EventStoreTelemetry.activitySource.StartActivity("eventStore.load")
                let observed = observeGeneration streamId
                use conn = new NpgsqlConnection(connString)
                let! rows = conn.QueryAsync<RawEvent>(selectAllSql, {| streamId = streamId |})
                let events = List.ofSeq rows
                let version =
                    events
                    |> List.tryLast
                    |> Option.map (fun e -> e.stream_version)
                    |> Option.defaultValue 0
                cachePut streamId observed (events, version)
                EventStoreTelemetry.recordLoad activity "db" streamId version (List.length events)
                return events, version
        }

    let insertEvents
            (conn: NpgsqlConnection) (tx: NpgsqlTransaction)
            (streamId: string) (expectedVersion: int) (events: 'TEvent list) : Task<int> =
        task {
            let mutable insertedCount = 0
            for (i, e) in events |> List.indexed do
                let data = JsonSerializer.Serialize<'TEvent>(e, jsonOptions)
                let parms =
                    {| stream_id = streamId
                       stream_version = expectedVersion + i + 1
                       data = data |}
                let! rows = conn.QueryAsync<int64>(insertSql, parms, tx)
                insertedCount <- insertedCount + Seq.length rows
            return insertedCount
        }

    /// Inserts + optional in-TX projection; on commit invalidates the scoped cache and returns the
    /// generation the appender may re-populate it under.
    let tryAppendCore (streamId: string) (expectedVersion: int) (events: 'TEvent list)
                      (projection: NpgsqlConnection -> NpgsqlTransaction -> Task) : Task<Result<int64 option, ConcurrencyConflict>> =
        task {
            if events.IsEmpty then return Ok (observeGeneration streamId)
            else
            use conn = new NpgsqlConnection(connString)
            do! conn.OpenAsync()
            use! tx = conn.BeginTransactionAsync()
            let! inserted = insertEvents conn tx streamId expectedVersion events
            if inserted < events.Length then
                do! tx.RollbackAsync()
                return Error ConcurrencyConflict
            else
                do! projection conn tx
                do! tx.CommitAsync()
                return Ok (invalidateStream streamId)
        }

    /// Begins a request-scoped identity-map scope. Repeated loads of the same stream within the
    /// returned scope are served from memory; dispose restores the prior scope (supports nesting).
    /// Intended to wrap one update handle — see the webhook entry point.
    member _.BeginRequestScope() : IDisposable =
        let prev = scopedCache.Value
        let cache = RequestCache()
        scopedCache.Value <- cache
        { new IDisposable with
            member _.Dispose() =
                cache.Dispose()
                scopedCache.Value <- prev }

    /// Returns the highest stream_version for the given stream, or 0 if the stream is empty.
    member _.GetStreamVersion(streamId: string) : Task<int> =
        task {
            use conn = new NpgsqlConnection(connString)
            let! version =
                conn.ExecuteScalarAsync<Nullable<int>>(maxVersionSql, {| streamId = streamId |})
            return if version.HasValue then version.Value else 0
        }

    /// Reads all RawEvents for a stream in version order, with the current version.
    /// `version = 0` means the stream does not exist yet.
    member _.GetRawEventsForStream(streamId: string) : Task<RawEvent list * int> =
        readStream streamId

    /// Reads all RawEvents for a stream on a caller-supplied connection/transaction, so a
    /// projection can see rows just inserted in the same TX. Ordered by stream_version.
    member _.ReadRawEventsForStream(conn: NpgsqlConnection, tx: NpgsqlTransaction, streamId: string) : Task<RawEvent list> =
        task {
            let! rows = conn.QueryAsync<RawEvent>(selectAllSql, {| streamId = streamId |}, tx)
            return List.ofSeq rows
        }

    /// Reads RawEvents for several streams in ONE round-trip on a caller-supplied
    /// connection/transaction, returning a `streamId -> RawEvent list` map (each list ordered by
    /// stream_version; absent streams map to an empty list). Lets a projection that needs sibling
    /// streams (e.g. message:* + moderation:*) fold them after a single query instead of N reads.
    member _.ReadRawEventsForStreams(conn: NpgsqlConnection, tx: NpgsqlTransaction, streamIds: string list) : Task<IReadOnlyDictionary<string, RawEvent list>> =
        task {
            let sql =
                $"""
SELECT id::BIGINT AS id, stream_id, stream_version, event_type, data::TEXT AS data, created_at
FROM {tableName}
WHERE stream_id = ANY(@streamIds)
ORDER BY stream_id, stream_version
"""
            let! rows = conn.QueryAsync<RawEvent>(sql, {| streamIds = List.toArray streamIds |}, tx)
            let byStream =
                rows
                |> Seq.groupBy (fun r -> r.stream_id)
                |> Seq.map (fun (sid, rs) -> sid, List.ofSeq rs)
                |> dict
            // Ensure every requested stream is present (empty when it has no events yet).
            let result = Dictionary<string, RawEvent list>()
            for sid in streamIds do
                result[sid] <- (match byStream.TryGetValue sid with | true, v -> v | _ -> [])
            return result :> IReadOnlyDictionary<string, RawEvent list>
        }

    /// Reads all events for a stream in version order, deserialized into `'TEvent`.
    member _.GetEventsForStream<'TEvent>(streamId: string) : Task<'TEvent[]> =
        task {
            let! (raws, _) = readStream streamId
            return
                raws
                |> List.map (fun r -> JsonSerializer.Deserialize<'TEvent>(r.data, jsonOptions))
                |> Array.ofList
        }

    /// Deserializes a single RawEvent — useful when callers have already fetched
    /// rows via custom SQL and need to fold them into typed state.
    member _.Deserialize<'TEvent>(raw: RawEvent) : 'TEvent =
        JsonSerializer.Deserialize<'TEvent>(raw.data, jsonOptions)

    /// Reads, deserializes, and folds the stream into aggregate state.
    member _.FoldEvents<'TEvent, 'State>
            (fold: 'State -> 'TEvent -> 'State, zero: 'State, streamId: string) : Task<'State> =
        task {
            let! (raws, _) = readStream streamId
            return
                raws
                |> List.map (fun r -> JsonSerializer.Deserialize<'TEvent>(r.data, jsonOptions))
                |> List.fold fold zero
        }

    /// INSERTs `events` at versions `expectedVersion + 1 .. expectedVersion + N`.
    /// Returns `Error ConcurrencyConflict` if any (stream_id, stream_version) collides
    /// with an existing row — caller's job is to re-read state and retry the
    /// read-decide-append cycle. Empty `events` is a no-op (`Ok ()`).
    member _.TryAppend<'TEvent>
            (streamId: string, expectedVersion: int, events: 'TEvent list)
            : Task<Result<unit, ConcurrencyConflict>> =
        task {
            match! tryAppendCore streamId expectedVersion events (fun _ _ -> Task.CompletedTask) with
            | Ok _ -> return Ok ()
            | Error e -> return Error e
        }

    /// Same as TryAppend, but runs `projection conn tx` after the inserts succeed and
    /// before the TX commits, so the caller's projection write lives in the same TX
    /// as the events. If `projection` throws, the whole TX rolls back and the
    /// exception propagates to the caller.
    /// Empty `events` is a no-op — projection is NOT called.
    member _.TryAppendWithProjection<'TEvent>
            (streamId: string, expectedVersion: int, events: 'TEvent list,
             projection: NpgsqlConnection -> NpgsqlTransaction -> Task)
            : Task<Result<unit, ConcurrencyConflict>> =
        task {
            match! tryAppendCore streamId expectedVersion events projection with
            | Ok _ -> return Ok ()
            | Error e -> return Error e
        }

    /// Read-decide-append-retry loop with optimistic concurrency. On conflict the
    /// stream is re-read and the decider is re-run, so the events being appended
    /// always reflect current state.
    /// task{} is hot/eager — recursion would blow the stack under contention,
    /// so iterations are driven by a `while`.
    member this.Transact<'TEvent, 'State>
            (fold: 'State -> 'TEvent -> 'State, zero: 'State,
             decider: 'State -> 'TEvent list, streamId: string)
            : Task<'TEvent list * 'State> =
        task {
            let mutable result = ValueNone
            while result.IsNone do
                let! (raws, version) = readStream streamId
                let state =
                    raws
                    |> List.map (fun r -> JsonSerializer.Deserialize<'TEvent>(r.data, jsonOptions))
                    |> List.fold fold zero
                let newEvents = decider state
                if newEvents.IsEmpty then
                    result <- ValueSome ([], state)
                else
                    match! tryAppendCore streamId version newEvents (fun _ _ -> Task.CompletedTask) with
                    | Ok observed ->
                        cacheAppend streamId observed raws version newEvents
                        let finalState = newEvents |> List.fold fold state
                        result <- ValueSome (newEvents, finalState)
                    | Error ConcurrencyConflict ->
                        // Stale read lost the version race — drop the cached entry so the retry
                        // re-reads the committed state fresh from the DB.
                        cacheEvict streamId
            return result.Value
        }

    /// Transact variant where the decider returns events plus an optional projection
    /// write. If events are non-empty and projection is `Some`, both happen in one TX.
    /// On a concurrency conflict the stream is re-read and the decider is re-run
    /// from scratch, so the projection callback always reflects the version
    /// the events are actually appended at — projection cannot drift from the log.
    member this.TransactWithProjection<'TEvent, 'State>
            (fold: 'State -> 'TEvent -> 'State, zero: 'State,
             decider: 'State -> 'TEvent list * (NpgsqlConnection -> NpgsqlTransaction -> Task) option,
             streamId: string)
            : Task<'TEvent list * 'State> =
        task {
            let mutable result = ValueNone
            while result.IsNone do
                let! (raws, version) = readStream streamId
                let state =
                    raws
                    |> List.map (fun r -> JsonSerializer.Deserialize<'TEvent>(r.data, jsonOptions))
                    |> List.fold fold zero
                let (newEvents, projection) = decider state
                if newEvents.IsEmpty then
                    result <- ValueSome ([], state)
                else
                    let proj =
                        match projection with
                        | Some p -> p
                        | None   -> fun _ _ -> Task.CompletedTask
                    match! tryAppendCore streamId version newEvents proj with
                    | Ok observed ->
                        cacheAppend streamId observed raws version newEvents
                        let finalState = newEvents |> List.fold fold state
                        result <- ValueSome (newEvents, finalState)
                    | Error ConcurrencyConflict ->
                        // Stale read lost the version race — drop the cached entry so the retry
                        // re-reads the committed state fresh from the DB.
                        cacheEvict streamId
            return result.Value
        }

    /// Loads state as snapshot + tail with the stream version (`0` = no stream yet), refreshing
    /// the snapshot once at least `policy.SnapshotEvery` events were folded past it.
    member _.LoadState<'TEvent, 'State>
            (fold: 'State -> 'TEvent -> 'State, zero: 'State, policy: SnapshotPolicy, streamId: string)
            : Task<'State * int> =
        task {
            let! loaded = loadSnapshotted fold zero policy streamId
            return unbox<'State> loaded.State, loaded.Version
        }

    /// Replays the whole log (ignoring any stored snapshot) and force-writes the snapshot when the
    /// stream has at least `policy.SnapshotEvery` events — backfill and repair for admin rebuilds.
    member _.RebuildSnapshot<'TEvent, 'State>
            (fold: 'State -> 'TEvent -> 'State, zero: 'State, policy: SnapshotPolicy, streamId: string)
            : Task<'State * int> =
        task {
            validatePolicy policy
            use activity = EventStoreTelemetry.activitySource.StartActivity("eventStore.snapshotRebuild")
            use conn = new NpgsqlConnection(connString)
            let! raws = conn.QueryAsync<RawEvent>(selectAllSql, {| streamId = streamId |})
            let raws = List.ofSeq raws
            let state =
                raws |> List.fold (fun s r -> fold s (JsonSerializer.Deserialize<'TEvent>(r.data, jsonOptions))) zero
            let version = raws |> List.tryLast |> Option.map _.stream_version |> Option.defaultValue 0
            if version >= policy.SnapshotEvery then
                let! _ = tryWriteSnapshot conn activity policy true streamId version state
                ()
            cacheEvict streamId
            return state, version
        }

    /// Snapshot-aware `TransactWithProjection`. The snapshot refresh runs after the commit, so a
    /// failing snapshot write can never roll back the appended events.
    member this.TransactWithProjection<'TEvent, 'State>
            (fold: 'State -> 'TEvent -> 'State, zero: 'State, policy: SnapshotPolicy,
             decider: 'State -> 'TEvent list * (NpgsqlConnection -> NpgsqlTransaction -> Task) option,
             streamId: string)
            : Task<'TEvent list * 'State> =
        task {
            let mutable result = None
            while result.IsNone do
                let! loaded = loadSnapshotted fold zero policy streamId
                let state = unbox<'State> loaded.State
                let (newEvents, projection) = decider state
                if newEvents.IsEmpty then
                    result <- Some ([], state)
                else
                    let proj =
                        match projection with
                        | Some p -> p
                        | None   -> fun _ _ -> Task.CompletedTask
                    match! tryAppendCore streamId loaded.Version newEvents proj with
                    | Ok observed ->
                        let finalState = newEvents |> List.fold fold state
                        let newVersion = loaded.Version + newEvents.Length
                        let! snapshotVersion =
                            if newVersion - loaded.SnapshotVersion >= policy.SnapshotEvery then
                                task {
                                    use activity = EventStoreTelemetry.activitySource.StartActivity("eventStore.snapshotWrite")
                                    use conn = new NpgsqlConnection(connString)
                                    let! written = tryWriteSnapshot conn activity policy false streamId newVersion finalState
                                    return if written then newVersion else loaded.SnapshotVersion
                                }
                            else Task.FromResult loaded.SnapshotVersion
                        stateCachePut streamId observed
                            { loaded with State = box finalState; Version = newVersion; SnapshotVersion = snapshotVersion }
                        result <- Some (newEvents, finalState)
                    | Error ConcurrencyConflict ->
                        // Stale read lost the version race — the retry must re-read from the DB.
                        cacheEvict streamId
            return result.Value
        }

    /// Snapshot-aware `Transact` (no projection).
    member this.Transact<'TEvent, 'State>
            (fold: 'State -> 'TEvent -> 'State, zero: 'State, policy: SnapshotPolicy,
             decider: 'State -> 'TEvent list, streamId: string)
            : Task<'TEvent list * 'State> =
        this.TransactWithProjection(fold, zero, policy, (fun s -> decider s, None), streamId)

/// Companion module — SRTP convenience that resolves Fold/Zero from the state type
/// at compile time, so callers don't have to thread them through every callsite.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module EventStore =
    /// Equivalent to `store.Transact((fun s e -> 'State.Fold(s, e)), 'State.Zero, decider, streamId)`.
    let inline appendEvent
            (store: EventStore) (streamId: string) (decider: 'State -> 'TEvent list)
            : Task<'TEvent list * 'State>
            when 'State : (static member Zero : 'State)
             and 'State : (static member Fold : 'State * 'TEvent -> 'State) =
        let fold s e = 'State.Fold(s, e)
        store.Transact(fold, 'State.Zero, decider, streamId)

    /// SRTP wrapper for FoldEvents.
    let inline foldEvents
            (store: EventStore) (streamId: string)
            : Task<'State>
            when 'State : (static member Zero : 'State)
             and 'State : (static member Fold : 'State * 'TEvent -> 'State) =
        let fold s e = 'State.Fold(s, e)
        store.FoldEvents<'TEvent, 'State>(fold, 'State.Zero, streamId)

    /// Equivalent to `appendEvent`, but the decider also returns an optional
    /// projection writer that runs in the same TX as the event inserts.
    let inline appendEventWithProjection
            (store: EventStore) (streamId: string)
            (decider: 'State -> 'TEvent list * (NpgsqlConnection -> NpgsqlTransaction -> Task) option)
            : Task<'TEvent list * 'State>
            when 'State : (static member Zero : 'State)
             and 'State : (static member Fold : 'State * 'TEvent -> 'State) =
        let fold s e = 'State.Fold(s, e)
        store.TransactWithProjection(fold, 'State.Zero, decider, streamId)

    /// SRTP wrapper for `store.LoadState`; type arguments are explicit because `'TEvent` can't be inferred.
    let inline loadSnapshottedState<'TEvent, 'State
                when 'State : (static member Zero : 'State)
                 and 'State : (static member Fold : 'State * 'TEvent -> 'State)>
            (store: EventStore) (policy: SnapshotPolicy) (streamId: string) : Task<'State * int> =
        let fold s e = 'State.Fold(s, e)
        store.LoadState<'TEvent, 'State>(fold, 'State.Zero, policy, streamId)

    /// Snapshot-aware `appendEventWithProjection`.
    let inline appendSnapshottedEventWithProjection
            (store: EventStore) (policy: SnapshotPolicy) (streamId: string)
            (decider: 'State -> 'TEvent list * (NpgsqlConnection -> NpgsqlTransaction -> Task) option)
            : Task<'TEvent list * 'State>
            when 'State : (static member Zero : 'State)
             and 'State : (static member Fold : 'State * 'TEvent -> 'State) =
        let fold s e = 'State.Fold(s, e)
        store.TransactWithProjection(fold, 'State.Zero, policy, decider, streamId)
