namespace BotInfra

open System
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open Dapper
open Npgsql

/// Raw row materialized from the per-bot event table.
/// `data` is JSONB read back as TEXT so Dapper can map it as a plain string.
[<CLIMutable>]
type RawEvent =
    { stream_id:      string
      stream_version: int
      event_type:     string
      data:           string
      created_at:     DateTime }

/// Sentinel returned by TryAppend when another writer already inserted at the
/// expected version. The caller's job is to re-read state and retry.
type ConcurrencyConflict = ConcurrencyConflict

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
type EventStore(connString: string, tableName: string, jsonOptions: JsonSerializerOptions) =
    do
        if isNull connString then nullArg (nameof connString)
        if isNull tableName then nullArg (nameof tableName)
        if isNull jsonOptions then nullArg (nameof jsonOptions)
        if not (Regex.IsMatch(tableName, @"^[a-z_][a-z0-9_]{0,62}$")) then
            invalidArg (nameof tableName) $"invalid event table name: %s{tableName}"

    let selectAllSql =
        $"""
SELECT stream_id, stream_version, event_type, data::TEXT AS data, created_at
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

    let readStream (streamId: string) : Task<RawEvent list * int> =
        task {
            use conn = new NpgsqlConnection(connString)
            let! rows = conn.QueryAsync<RawEvent>(selectAllSql, {| streamId = streamId |})
            let events = List.ofSeq rows
            let version =
                events
                |> List.tryLast
                |> Option.map (fun e -> e.stream_version)
                |> Option.defaultValue 0
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
            if events.IsEmpty then return Ok ()
            else
            use conn = new NpgsqlConnection(connString)
            do! conn.OpenAsync()
            use! tx = conn.BeginTransactionAsync()
            let! inserted = insertEvents conn tx streamId expectedVersion events
            if inserted < events.Length then
                do! tx.RollbackAsync()
                return Error ConcurrencyConflict
            else
                do! tx.CommitAsync()
                return Ok ()
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
            if events.IsEmpty then return Ok ()
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
                return Ok ()
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
                    match! this.TryAppend(streamId, version, newEvents) with
                    | Ok _ ->
                        let finalState = newEvents |> List.fold fold state
                        result <- ValueSome (newEvents, finalState)
                    | Error ConcurrencyConflict -> ()
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
                    match! this.TryAppendWithProjection(streamId, version, newEvents, proj) with
                    | Ok _ ->
                        let finalState = newEvents |> List.fold fold state
                        result <- ValueSome (newEvents, finalState)
                    | Error ConcurrencyConflict -> ()
            return result.Value
        }

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
