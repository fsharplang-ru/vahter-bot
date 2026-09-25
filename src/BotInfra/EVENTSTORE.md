# BotInfra.EventStore

A small, opinionated wrapper for append-only event sourcing on top of a
per-bot Postgres event table. Used by VahterBanBot today; designed so other
bots (alita) can adopt it as-is.

The wrapper does **not** provide a single shared event table — each consuming
bot owns its own table in its own database. The wrapper is generic over the
table name and the event DU.

## Required event-table schema

Every consumer must create exactly this shape (column order doesn't matter,
but every column and the unique key are load-bearing):

```sql
CREATE TABLE event (
    id              BIGSERIAL   PRIMARY KEY,
    stream_id       TEXT        NOT NULL,
    stream_version  INT         NOT NULL,
    event_type      TEXT        GENERATED ALWAYS AS (data->>'Case') STORED,
    data            JSONB       NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (stream_id, stream_version)
);

CREATE INDEX idx_event_stream ON event(stream_id, id);
CREATE INDEX idx_event_type   ON event(event_type, created_at);

-- Don't forget the GRANT on the service role:
GRANT SELECT, INSERT ON event TO <bot_service_role>;
GRANT USAGE,  SELECT ON SEQUENCE event_id_seq TO <bot_service_role>;
```

The table name is configurable (`"event"` is just the default vahter uses) but
must match `^[a-z_][a-z0-9_]{0,62}$`. Functional indexes on JSONB payload
fields (e.g. `((data->>'userId')::BIGINT) WHERE event_type = 'X'`) are
bot-specific and stay in each bot's migrations.

## JSON payload convention

The wrapper is JSON-library-agnostic — you pass in a `JsonSerializerOptions` —
but the schema's `event_type` column is a Postgres GENERATED column reading
`data->>'Case'`. So whatever serializer you use **must** emit the union case
discriminator as a top-level field named `Case`. With FSharp.SystemTextJson:

```fsharp
let eventJsonOpts =
    JsonFSharpOptions.Default()
        .WithUnionInternalTag()           // produces "Case" discriminator
        .WithUnionUnwrapRecordCases()
        .WithUnionNamedFields()
        .WithUnwrapOption()
        .WithSkippableOptionFields(SkippableOptionFields.Always, deserializeNullAsNone = true)
        .ToJsonSerializerOptions()
```

Any other shape silently breaks the GENERATED column (it stays NULL) and
every `WHERE event_type = 'Foo'` query starts returning empty.

## Stream id format

Up to the bot. Vahter uses `"user:{id}"`, `"message:{chatId}:{messageId}"`,
`"moderation:{chatId}:{messageId}"`, `"callback:{guid}"`, etc. Pick a prefix
per aggregate and stick to it.

## Aggregate state convention

For the SRTP convenience wrapper `EventStore.appendEvent`, the state record
must have two static members:

```fsharp
type User =
    { Id: int64; Username: string option; Banned: ... }
    static member Zero = { Id = 0L; Username = None; Banned = None }
    static member Fold (s: User, e: UserEvent) : User =
        match e with
        | UsernameChanged x -> { s with Username = x.username }
        | UserBanned x      -> { s with Banned = Some (x.actor, x.bannedAt) }
        | ...
```

If you'd rather pass `fold` and `zero` explicitly (e.g. when state lives
in another file), use `store.Transact` and `store.FoldEvents` directly —
they take both as ordinary arguments.

## Minimal usage

```fsharp
open BotInfra

// Construction — once per DbService.
let eventJsonOpts = ... // see above
let store = EventStore(connString, "event", eventJsonOpts)

// Append (read-decide-append-retry) — SRTP form.
let! (newEvents, finalState) =
    EventStore.appendEvent store $"user:{userId}" (fun (state: User) ->
        if state.Username = newUsername then []
        else [ UsernameChanged {| userId = userId; username = newUsername |} ])

// Fold a stream into state.
let! state = EventStore.foldEvents<UserEvent, User> store $"user:{userId}"
// Or with explicit fold/zero:
let! state = store.FoldEvents(User.Fold, User.Zero, $"user:{userId}")

// Read raw events.
let! (raws, version) = store.GetRawEventsForStream $"user:{userId}"

// Lower-level append at a known version (caller manages retry):
match! store.TryAppend($"user:{userId}", expectedVersion, [evt]) with
| Ok ()                     -> ()
| Error ConcurrencyConflict -> () // re-read, re-decide, retry
```

## Transactional projection writes

When you maintain a projection table and need it to stay in lockstep with the
event log, use `TransactWithProjection`. The decider returns the events to
append *and* a callback that performs the projection write — both happen
inside one TX, so a crash between the two is impossible:

```fsharp
let! (events, state) =
    store.TransactWithProjection(
        User.Fold, User.Zero,
        decider = (fun (s: User) ->
            if s.Username = newUsername then [], None
            else
                let evt = UsernameChanged {| userId = userId; username = newUsername |}
                let projection (conn: NpgsqlConnection) (tx: NpgsqlTransaction) = task {
                    let! _ = conn.ExecuteAsync(
                        "UPDATE user_projection SET username = @u WHERE id = @id",
                        {| u = newUsername; id = userId |}, tx)
                    return ()
                }
                [evt], Some projection),
        streamId = $"user:{userId}")
```

If the events are appended successfully but the projection callback throws,
the whole TX rolls back and the events are not persisted. On a concurrency
conflict the stream is re-read and the decider is re-run from scratch, so
the projection callback always reflects the actual append version.

If the decider returns `[]`, no events are written and the projection is
not invoked — events and projection cannot drift.

## Snapshots (opt-in)

Without a policy every load folds the entire stream. For long-lived aggregates
(vahter's `user:*` streams run into thousands of events), pass a snapshot table
and use the `SnapshotPolicy` overloads: state is loaded as *stored snapshot +
the events after it*, in one statement.

```sql
CREATE TABLE event_snapshot (
    stream_id       TEXT        NOT NULL,
    state_type      TEXT        NOT NULL,
    schema_version  INT         NOT NULL,
    stream_version  INT         NOT NULL,
    state           JSONB       NOT NULL,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (stream_id, state_type)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON event_snapshot TO <bot_service_role>;

-- Snapshots are only valid for an append-only log: any in-place rewrite of events drops them.
CREATE OR REPLACE FUNCTION event_snapshot_invalidate() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'TRUNCATE' THEN
        TRUNCATE event_snapshot;
    ELSE
        DELETE FROM event_snapshot WHERE stream_id = OLD.stream_id OR stream_id = NEW.stream_id;
    END IF;
    RETURN NULL;
END $$;
CREATE OR REPLACE TRIGGER event_snapshot_invalidate AFTER UPDATE OR DELETE ON event
    FOR EACH ROW EXECUTE FUNCTION event_snapshot_invalidate();
CREATE OR REPLACE TRIGGER event_snapshot_invalidate_truncate AFTER TRUNCATE ON event
    FOR EACH STATEMENT EXECUTE FUNCTION event_snapshot_invalidate();
```

```fsharp
let store = EventStore(connString, "event", eventJsonOpts, "event_snapshot")

type User =
    ...
    static member SnapshotPolicy = { StateType = "User"; SchemaVersion = 1; SnapshotEvery = 20 }

let! (state, version) = EventStore.loadSnapshottedState<UserEvent, User> store User.SnapshotPolicy streamId
let! (events, state)  = EventStore.appendSnapshottedEventWithProjection store User.SnapshotPolicy streamId decider
// explicit fold/zero: store.LoadState / store.Transact / store.TransactWithProjection overloads
```

Rules the implementation guarantees:

- **Snapshots are a cache, never the source of truth.** Truncating the table is
  always safe; loads replay and re-create rows. A snapshot write failure is
  recorded (`eventstore_snapshot_ops_total{op="write_failed"}` + span error) and
  swallowed — it never fails a load, and on the append path it runs *after* the
  commit so it can never roll back events.
- **Stale snapshots are detected and discarded.** A row whose `schema_version`
  differs from the policy is ignored (and overwritten by the next write). A row
  that is ahead of its log or doesn't deserialize is ignored, the stream is
  replayed and the row force-overwritten (`op="discarded_ahead" |
  "discarded_unreadable"`); a failure there is recorded, never thrown.
- **Rewriting history drops snapshots.** The triggers above delete a stream's
  snapshots on any `UPDATE`/`DELETE` of its events (and `TRUNCATE` clears the
  table), so a data-fix migration can never leave a snapshot that disagrees
  with the rewritten log. The bot role never updates events, so the triggers
  only fire during migrations.
- **Snapshots only move forward** within a schema version (upsert guard), so a
  slow writer can't regress a newer snapshot. A different schema version (new
  code or a rollback) always overwrites.
- **Consistent reads.** Snapshot and tail come from one SQL statement, so a
  concurrent snapshot rewrite can't make the tail skip events. Optimistic
  concurrency is unchanged: expected version = snapshot version + tail length.
- **Write cadence.** A snapshot is written when a load or a committed append
  leaves at least `SnapshotEvery` events past the stored one — frozen streams
  get snapshotted lazily on their first slow load, no backfill needed.
- **Request scope.** `BeginRequestScope` caches snapshot-loaded states next to
  raw streams; every committed append (through any API) drops both cached views
  of that stream, then the appending path re-populates its own. A load only
  publishes into the cache if no append invalidated the stream since it read
  the DB, so a slow parallel load can't put pre-append state back. A disposed
  scope stops caching, so fire-and-forget work that captured it reads the DB.
- **Known costs.** While pods with two different `SchemaVersion`s run side by
  side (rolling deploy of a bump), each treats the other's row as a miss, so
  long streams replay until the rollout finishes. `TryAppend` with an
  `expectedVersion` above the head can create a version gap; the Transact
  loops never do — prefer them.

### Versioning — the one thing you must not forget

A snapshot bakes in two things: the state type's *shape* and the *result of
its fold*. Bump `SchemaVersion` on any change to either — a new field silently
deserializes to its default, and a fixed fold bug stays baked into old
snapshots. Enforce it with two pin tests next to the aggregate (see
`tests/VahterBanBot.Unit.Tests/UserSnapshotTests.fs`):

1. `SnapshotShape.describe typeof<State>` pinned per schema version.
2. A fold *transcript* pinned per schema version: the snapshot JSON of every
   intermediate state of a canonical sequence (every event case plus edge
   cases), and of each event applied to `Zero` and to a fully-populated state.
   Pinning only a final state misses changes whose effect is overwritten later;
   pinning JSON also catches serializer-option changes.

Both assert the *latest* pin's version equals the policy's, so changing the
type or the fold fails until the version is bumped and a new pin appended.
Also keep a JSON round-trip test and a "snapshot + tail = full replay at every
split point" test.

## What this wrapper does NOT do

- **Cross-stream transactions.** One stream per call. If you need to write
  to two streams atomically, model them as one stream.
- **Schema migrations** of payload shapes. If you change a DU case, write a
  separate backfill migration (vahter has several — see V25..V28 in
  `src/vahter-bot/migrations/`).
- **Event publishing / outbox.** Add your own `outbox` table and write to it
  in the projection callback if you need this.
