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

## What this wrapper does NOT do

- **Snapshots / state caching.** Every read folds the entire stream. If you
  have very long streams, project to a separate read model.
- **Cross-stream transactions.** One stream per call. If you need to write
  to two streams atomically, model them as one stream.
- **Schema migrations** of payload shapes. If you change a DU case, write a
  separate backfill migration (vahter has several — see V25..V28 in
  `src/vahter-bot/migrations/`).
- **Event publishing / outbox.** Add your own `outbox` table and write to it
  in the projection callback if you need this.
