-- Widen all Telegram message-id columns from INT to BIGINT.
-- Telegram message ids are 64-bit (Bot API); the upcoming Funogram migration
-- carries them as int64 end-to-end, and the app code now uses int64 too.
-- The event table itself is untouched (ids live inside JSONB) — only typed
-- columns, the functional index expression, and the debug views change.

-- Legacy pre-event-sourcing tables (kept for history, widened for consistency).
ALTER TABLE "message"               ALTER COLUMN message_id        TYPE BIGINT;
ALTER TABLE banned                  ALTER COLUMN message_id        TYPE BIGINT;
ALTER TABLE false_negative_messages ALTER COLUMN message_id        TYPE BIGINT;
ALTER TABLE banned_by_bot           ALTER COLUMN message_id        TYPE BIGINT;
ALTER TABLE callback                ALTER COLUMN action_message_id TYPE BIGINT;
ALTER TABLE vahter_actions          ALTER COLUMN target_message_id TYPE BIGINT;

-- Live tables.
ALTER TABLE llm_triage              ALTER COLUMN message_id        TYPE BIGINT;
ALTER TABLE snapshot_message        ALTER COLUMN message_id        TYPE BIGINT;

-- Functional index on the event JSONB payload: the app SQL now casts
-- (data->>'actionMessageId')::BIGINT, so the index expression must match
-- exactly or the planner won't use it (see DB.fs ExpireCallbacksByMessageId).
DROP INDEX IF EXISTS idx_event_callback_posted_msg;
CREATE INDEX idx_event_callback_posted_msg
    ON event(((data->>'actionMessageId')::BIGINT))
    WHERE event_type = 'CallbackMessagePosted';

-- Debug views (V31) exposing message_id: a view column type can't change via
-- CREATE OR REPLACE, so drop and recreate with ::bigint casts.
DROP VIEW IF EXISTS v_messages;
CREATE VIEW v_messages AS
SELECT
    (data->>'chatId')::bigint    AS chat_id,
    (data->>'messageId')::bigint AS message_id,
    (data->>'userId')::bigint    AS user_id,
    data->>'text'                AS text,
    data->>'rawMessage'          AS raw_message,
    created_at
FROM event
WHERE event_type = 'MessageReceived'
ORDER BY created_at DESC;

DROP VIEW IF EXISTS v_recent_actions;
CREATE VIEW v_recent_actions AS
SELECT
    e.created_at,
    e.event_type,
    (e.data->>'chatId')::bigint                                                   AS chat_id,
    (e.data->>'messageId')::bigint                                                AS message_id,
    COALESCE((e.data->>'userId')::bigint, (e.data->>'targetUserId')::bigint)      AS user_id,
    (e.data->>'vahterId')::bigint                                                 AS vahter_id,
    e.data->'reason'->>'Case'                                                     AS auto_delete_reason,
    e.data->>'verdict'                                                            AS llm_verdict,
    (e.data->>'score')::float                                                     AS ml_score
FROM event e
WHERE e.event_type IN (
    'VahterActed',
    'BotAutoDeleted',
    'MessageMarkedSpam',
    'MessageMarkedHam',
    'LlmClassified',
    'MlScoredMessage'
)
ORDER BY e.created_at DESC;
