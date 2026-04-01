# Event Sourcing Migration — vahter-bot

## Context

The bot has accumulated 22 migrations and 11+ tables that overlap in purpose. Multiple tables track "who got banned" (`banned`, `banned_by_bot`), "what the bot saw" (`message`, `false_positive_messages`, `false_negative_messages`), and "what a vahter did" (`vahter_actions`, `callback`). The ML training query joins 5+ tables with FULL OUTER JOINs and computed columns because ban history is spread across many tables.

Goal: one authoritative append-only `event` table. Legacy tables kept as frozen historical snapshots (rollback safety — old code can always resume writing to them).

---

## Approach: Single Rewrite + Maintenance Window

No dual-write phase. During silent hours:
1. Bot goes down
2. V23 migration runs: creates `event` table + backfills all historical data
3. New codebase deployed (writes only to `event` from now on)
4. Bot comes back up

**Rollback:** redeploy old code → resumes reading/writing legacy tables exactly as before.

---

## Event Table Schema

```sql
CREATE TABLE IF NOT EXISTS event (
    id             BIGSERIAL PRIMARY KEY,
    stream_id      TEXT NOT NULL,
    stream_version INT  NOT NULL,
    event_type     TEXT NOT NULL,
    data           JSONB NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (stream_id, stream_version)
);
```

**Indexes** — two baseline indexes ship with the table. Additional indexes should be derived from actual query patterns during implementation:

```sql
CREATE INDEX IF NOT EXISTS idx_event_stream ON event(stream_id, id);
CREATE INDEX IF NOT EXISTS idx_event_type   ON event(event_type, created_at);
-- Implementation agent: add more indexes as queries are written (e.g. (stream_id, event_type), (event_type, stream_id) for "did X happen to stream Y" lookups)
```

---

## Stream Design

| Stream ID | Events |
|---|---|
| `user:{userId}` | `UserSeen`, `UserBanned`, `UserUnbanned`, `UserReactionRecorded` |
| `message:{chatId}:{messageId}` | `MessageReceived`, `MessageDeleted`, `MessageMarkedSpam`, `MessageMarkedHam` |
| `moderation:{chatId}:{messageId}` | `VahterActed`, `BotAutoDeleted`, `CallbackCreated`, `CallbackResolved` |
| `detection:{chatId}:{messageId}` | `MlScoredMessage`, `LlmClassified`, `InvisibleMentionDetected` |

---

## Domain Event Catalog

### Stream: `user:{userId}`
| Event type | Replaces | Key data |
|---|---|---|
| `UserSeen` | `user` upsert | `{userId, username}` |
| `UserBanned` | `banned` + `banned_by_bot` | `{userId, bannedBy, reason, chatId, messageId, messageText, isBotBan}` |
| `UserUnbanned` | `unbanUser` | `{userId, unbannedBy}` |
| `UserReactionRecorded` | `reaction_count` update | `{userId, totalCount}` |

### Stream: `message:{chatId}:{messageId}`
| Event type | Replaces | Key data |
|---|---|---|
| `MessageReceived` | `message` insert | `{chatId, messageId, userId, text, rawMessage}` |
| `MessageDeleted` | scattered delete calls | `{chatId, messageId, deletedBy}` |
| `MessageMarkedSpam` | `false_negative_messages` | `{chatId, messageId, markedBy}` |
| `MessageMarkedHam` | `false_positive_messages` (when message context is known) | `{chatId, messageId, text, markedBy}` |

### Stream: `moderation:{chatId}:{messageId}`
| Event type | Replaces | Key data |
|---|---|---|
| `VahterActed` | `vahter_actions` | `{vahterId, actionType, targetUserId, chatId, messageId}` |
| `BotAutoDeleted` | `banned_by_bot` | `{chatId, messageId, userId, score, reason}` |
| `CallbackCreated` | `callback` insert | `{id, data, targetUserId, actionChannelId}` |
| `CallbackResolved` | `callback` delete | `{id}` |

### Stream: `detection:{chatId}:{messageId}`
| Event type | Replaces | Key data |
|---|---|---|
| `MlScoredMessage` | inline ML logging | `{chatId, messageId, score, isSpam}` |
| `LlmClassified` | `llm_triage` | `{chatId, messageId, verdict, promptTokens, completionTokens, latencyMs}` |
| `InvisibleMentionDetected` | implicit in kill flow | `{chatId, messageId, userId}` |

Migration strategy for `false_positive_messages`: JOIN against `message` table on text hash. Rows that match get a proper `MessageMarkedHam` event in the `message:{chatId}:{messageId}` stream. Rows with no match (message already cleaned up) are simply not migrated — that data is lost, which is acceptable.

---

## Aggregates, Folds, and the `transact` Pattern

### Per-stream aggregate pattern

Each stream type has three things:
1. **A DU for events** — enables exhaustiveness checking at compile time
2. **A state/aggregate type** — the folded in-memory view of the stream
3. **A `fold` function** — reconstructs state from events (never persisted, always recomputed)

Example for the `user:` stream:

```fsharp
// 1. Event DU — exhaustiveness enforced by compiler
type UserEvent =
    | UserSeen     of {| userId: int64; username: string option |}
    | UserBanned   of {| userId: int64; bannedBy: int64; isBotBan: bool; chatId: int64 option; messageId: int option; messageText: string |}
    | UserUnbanned of {| userId: int64; unbannedBy: int64 |}
    | UserReactionRecorded of {| userId: int64; totalCount: int |}

// 2. Aggregate — what the logic actually cares about
type UserState = {
    IsBanned:      bool
    BannedBy:      int64 option
    Username:      string option
    ReactionCount: int
}

// 3. Fold — pure, total, called on every transact (not persisted anywhere)
let foldUser (state: UserState) (event: UserEvent) : UserState =
    match event with
    | UserSeen e            -> { state with Username = e.username }
    | UserBanned e          -> { state with IsBanned = true; BannedBy = Some e.bannedBy }
    | UserUnbanned _        -> { state with IsBanned = false; BannedBy = None }
    | UserReactionRecorded e -> { state with ReactionCount = e.totalCount }

let initialUserState = { IsBanned = false; BannedBy = None; Username = None; ReactionCount = 0 }
```

Same pattern applies for every stream: `ModerationState`, `MessageState`, `DetectionState`, etc.

### The `transact` function

```fsharp
// Core store primitives (raw DB layer)
let readStream  (streamId: string) : Task<RawEvent list * int>   // raw rows + current version
let tryAppend   (streamId: string) (expectedVersion: int) (events: RawEvent list) : Task<Result<unit, ConcurrencyConflict>>

// Generic transact — decider receives the AGGREGATE, not raw events
// Note: task{} is hot/eager — use while loop, not recursion
let transact
    (deserialize: RawEvent -> 'Event)
    (fold:        'State -> 'Event -> 'State)
    (initial:     'State)
    (decider:     'State -> 'Event list)      // pure: state → new events to append
    (streamId:    string)
    : Task<'Event list> =
    task {
        let mutable result = ValueNone
        while result.IsNone do
            let! (rawEvents, version) = readStream streamId
            let state   = rawEvents |> List.map deserialize |> List.fold fold initial
            let newEvents = decider state                      // acts on typed aggregate
            let rawNew    = newEvents |> List.map serialize
            match! tryAppend streamId version rawNew with
            | Ok _                      -> result <- ValueSome newEvents
            | Error ConcurrencyConflict -> ()                  // re-read, re-decide
        return result.Value
    }
```

The `decider` never sees raw events — it sees a clean aggregate. The list structure concern is irrelevant here: `readStream` returns events for folding (read-only), and `decider` returns `[]` or `[NewEvent]` (tiny).

**Note on F# tasks:** `task {}` is hot and eagerly scheduled. Never use `rec` + `return! f` — use `while` loop instead.

### Usage example — two vahters clicking KILL on the same message

```fsharp
let killMessage vahterId chatId msgId =
    transact
        deserializeModerationEvent
        foldModeration
        initialModerationState
        (fun state ->
            if state.VahterActed then []   // already acted — no-op
            else [VahterActed { vahterId=vahterId; actionType="potential_kill"; ... }])
        $"moderation:{chatId}:{msgId}"
```

- First vahter: state has `VahterActed = false` → appends event → success
- Second vahter: `ConcurrencyConflict` → re-reads → state has `VahterActed = true` → `decider` returns `[]` → exits cleanly

Exceptions (DB down, etc.) still bubble and break the call stack — only `ConcurrencyConflict` loops.

---

## Event Schema Evolution

No explicit `version` field in event payloads. Since this codebase has a single team, the rule is: **only add optional fields to existing DU cases, never rename or remove fields**. `FSharp.SystemTextJson` (already in the project) handles missing optional fields gracefully on deserialization. Breaking changes require a new event type name, not modification of an existing one.

---

## Data Migration — Idempotency Strategy

All backfill INSERTs use `ON CONFLICT (stream_id, stream_version) DO NOTHING`. The `stream_version` for historical data is **deterministic** (computed from source data ordering), so re-running the same insert produces the same `stream_version` value and hits the UNIQUE constraint → skips gracefully.

This means:
- **Scenario A (pre-run + CI/CD noop):** Run migration manually before deployment. CI/CD runs Flyway, sees V23 already applied, skips entirely.
- **Scenario B (CI/CD retry after timeout/failure):** Migration resumes from where it left off because already-inserted events are skipped via `ON CONFLICT DO NOTHING`. Partially-inserted batches are safe.

`CREATE TABLE IF NOT EXISTS` and `CREATE INDEX IF NOT EXISTS` handle schema-level idempotency.

### Backfill SQL (V23)

```sql
-- 1. UserSeen — from user table
INSERT INTO event(stream_id, stream_version, event_type, data, created_at)
SELECT
    'user:' || id,
    ROW_NUMBER() OVER (PARTITION BY id ORDER BY created_at),
    'UserSeen',
    jsonb_build_object('userId', id, 'username', username),
    created_at
FROM "user"
ON CONFLICT (stream_id, stream_version) DO NOTHING;

-- 2. MessageReceived — from message table (always version 1 per stream)
INSERT INTO event(stream_id, stream_version, event_type, data, created_at)
SELECT
    'message:' || chat_id || ':' || message_id,
    1,
    'MessageReceived',
    jsonb_build_object('chatId', chat_id, 'messageId', message_id,
                       'userId', user_id, 'text', text, 'rawMessage', raw_message::jsonb),
    created_at
FROM message
ON CONFLICT (stream_id, stream_version) DO NOTHING;

-- 3. UserBanned — union manual bans + bot bans, assign version per user ordered by time
-- Both sources share the same user: stream so versions MUST be assigned together via a UNION
-- (doing them separately would produce version collisions for users who were banned by both)
WITH all_bans AS (
    SELECT banned_user_id AS user_id,
           banned_at,
           banned_by,
           false          AS is_bot_ban,
           banned_in_chat_id,
           message_id,
           message_text
    FROM banned
    UNION ALL
    SELECT banned_user_id,
           banned_at,
           NULL,          -- no vahter for bot bans
           true,
           banned_in_chat_id,
           message_id,
           message_text
    FROM banned_by_bot
),
numbered AS (
    SELECT *,
           -- offset by 1 because UserSeen is always version 1 (user table has one row per user)
           1 + ROW_NUMBER() OVER (PARTITION BY user_id ORDER BY banned_at) AS stream_ver
    FROM all_bans
)
INSERT INTO event(stream_id, stream_version, event_type, data, created_at)
SELECT
    'user:' || user_id,
    stream_ver,
    'UserBanned',
    jsonb_build_object('userId', user_id, 'bannedBy', banned_by, 'isBotBan', is_bot_ban,
                       'chatId', banned_in_chat_id, 'messageId', message_id, 'messageText', message_text),
    banned_at
FROM numbered
ON CONFLICT (stream_id, stream_version) DO NOTHING;

-- 4. MessageMarkedSpam — from false_negative_messages (version 2, MessageReceived is always 1)
INSERT INTO event(stream_id, stream_version, event_type, data, created_at)
SELECT
    'message:' || chat_id || ':' || message_id,
    2,
    'MessageMarkedSpam',
    jsonb_build_object('chatId', chat_id, 'messageId', message_id, 'markedBy', 0),
    NOW()  -- no timestamp in source table
FROM false_negative_messages
ON CONFLICT (stream_id, stream_version) DO NOTHING;

-- 5. MessageMarkedHam — match false_positive_messages back to message table
-- Use a temp table of pre-computed message hashes to avoid O(M*N) text comparisons
-- false_positive_messages is already keyed by md5(text)::uuid, so match on hash
CREATE TEMP TABLE msg_hash_lookup AS
    SELECT chat_id, message_id, md5(text)::uuid AS text_hash
    FROM message
    WHERE text IS NOT NULL;
CREATE INDEX ON msg_hash_lookup(text_hash);

-- Unmatched rows (message already cleaned up) are simply not migrated — data loss accepted
INSERT INTO event(stream_id, stream_version, event_type, data, created_at)
SELECT
    'message:' || m.chat_id || ':' || m.message_id,
    2,
    'MessageMarkedHam',
    jsonb_build_object('chatId', m.chat_id, 'messageId', m.message_id, 'text', fp.text, 'markedBy', 0),
    NOW()
FROM false_positive_messages fp
JOIN msg_hash_lookup m ON m.text_hash = fp.text_hash
ON CONFLICT (stream_id, stream_version) DO NOTHING;

DROP TABLE msg_hash_lookup;

-- 6. VahterActed — from vahter_actions (always version 1 per moderation stream)
INSERT INTO event(stream_id, stream_version, event_type, data, created_at)
SELECT
    'moderation:' || target_chat_id || ':' || target_message_id,
    1,
    'VahterActed',
    jsonb_build_object('vahterId', vahter_id, 'actionType', action_type,
                       'targetUserId', target_user_id, 'chatId', target_chat_id, 'messageId', target_message_id),
    created_at
FROM vahter_actions
ON CONFLICT (stream_id, stream_version) DO NOTHING;

-- 7. LlmClassified — from llm_triage
INSERT INTO event(stream_id, stream_version, event_type, data, created_at)
SELECT
    'detection:' || chat_id || ':' || message_id,
    1,
    'LlmClassified',
    jsonb_build_object('chatId', chat_id, 'messageId', message_id, 'verdict', verdict,
                       'promptTokens', prompt_tokens, 'completionTokens', completion_tokens, 'latencyMs', latency_ms),
    created_at
FROM llm_triage
ON CONFLICT (stream_id, stream_version) DO NOTHING;
```

**Tables NOT backfilled (kept as-is, new code still uses them):**
- `ml_trained_model` — operational singleton; new code keeps using it
- `scheduled_job` — job locking; new code keeps using it
- `bot_setting` — configuration; new code keeps using it
- `callback` — stale during downtime, new code handles via `moderation` stream events

**Legacy tables kept frozen (never written to again):**
`banned`, `banned_by_bot`, `message`, `false_positive_messages`, `false_negative_messages`, `vahter_actions`, `llm_triage`, `callback`

---

## Cleanup Job Changes

Message cleanup is removed entirely — storage is cheap and a year+ retention is fine. If archiving becomes necessary that's a future problem.

**Only cleanup that remains:** orphaned callbacks — `moderation:{chatId}:{messageId}` streams where `CallbackCreated` exists but no `CallbackResolved` and `created_at > N hours`. These are vahter action messages that were never resolved (bot restart, etc.) and have no value after the fact.

The existing `CLEANUP_OLD_MESSAGES` / `CLEANUP_OLD_LIMIT_SEC` / `DETECTED_SPAM_CLEANUP_AGE_HOURS` settings and related cleanup code in `Cleanup.fs` can be removed.

---

## ML Training Query

**After migration:**
```sql
SELECT
    e_msg.data->>'text'   AS text,
    e_msg.data->>'userId' AS userId,
    -- custom emoji count still needs JSONB lateral on rawMessage entities
    (SELECT COUNT(*) FROM jsonb_array_elements(e_msg.data->'rawMessage'->'entities') ent
     WHERE ent->>'type' = 'custom_emoji') AS customEmojiCount,
    EXISTS(
        SELECT 1 FROM event e2
        WHERE e2.stream_id = e_msg.stream_id
          AND e2.event_type IN ('BotAutoDeleted', 'VahterActed')
          AND (e2.data->>'actionType' IS NULL
               OR e2.data->>'actionType' IN ('potential_kill', 'manual_ban'))
    ) AS isSpam
FROM event e_msg
WHERE e_msg.event_type = 'MessageReceived'
  AND e_msg.created_at > NOW() - INTERVAL '30 days'
  AND NOT EXISTS(
      SELECT 1 FROM event e_fp
      WHERE e_fp.event_type = 'MessageMarkedHam'
        AND e_fp.data->>'text' = e_msg.data->>'text'
  )
```

The 6-table JOIN with FULL OUTER collapses to two EXISTS subqueries.

---

## Key Files to Modify

| File | Change |
|---|---|
| `src/migrations/V23__events.sql` | `CREATE TABLE IF NOT EXISTS event`, backfill inserts with `ON CONFLICT DO NOTHING` |
| `src/VahterBanBot/Types.fs` | Add F# DU `BotEvent` with all event cases; keep or remove legacy DB types as implementation proceeds |
| `src/VahterBanBot/DB.fs` | Add `readStream`, `tryAppend`, `transact`; replace all write functions with transact-based equivalents; update ML and stats queries |
| `src/VahterBanBot/Bot.fs` | All state-changing operations use `transact`; `ConcurrencyConflict` handled in the loop, not in callers |
| `src/VahterBanBot/ML.fs` | Update `mlData` to use simplified event query |
| `src/VahterBanBot/Cleanup.fs` | Update cleanup/stats queries to read from events; add orphaned callback cleanup |
| `src/VahterBanBot.Tests/ContainerTestBase.fs` | Update DB helper assertions to query `event` table |
| `src/VahterBanBot.Tests/test_seed.sql` | Replace legacy table inserts with event inserts |

---

## Verification

1. Run V23 migration on a copy of prod DB; verify event counts match legacy table row counts; re-run migration to confirm idempotency (all inserts are NO-OPs)
2. Run full test suite — all tests should pass (logic unchanged)
3. Verify ML training produces same spam/ham label distribution before and after migration
4. Manual smoke test: message → vahter action → events appear with correct stream/version
5. Race condition test: two concurrent requests on same message → one `VahterActed` event, one graceful "already handled" log
