-- V23: Event sourcing migration
-- Creates the append-only event table and backfills historical data from legacy tables.
-- All INSERTs use ON CONFLICT DO NOTHING for idempotency (safe to re-run).

CREATE TABLE IF NOT EXISTS event (
    id             BIGSERIAL PRIMARY KEY,
    stream_id      TEXT NOT NULL,
    stream_version INT  NOT NULL,
    event_type     TEXT GENERATED ALWAYS AS (data->>'Case') STORED,
    data           JSONB NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (stream_id, stream_version)
);

CREATE INDEX IF NOT EXISTS idx_event_stream ON event(stream_id, id);
CREATE INDEX IF NOT EXISTS idx_event_type   ON event(event_type, created_at);
CREATE INDEX IF NOT EXISTS idx_event_stream_type ON event(stream_id, event_type);

-- GRANT permissions to bot user
GRANT SELECT, INSERT ON event TO vahter_bot_ban_service;
GRANT USAGE, SELECT ON SEQUENCE event_id_seq TO vahter_bot_ban_service;

-- 1. UsernameChanged — from user table
INSERT INTO event(stream_id, stream_version, data, created_at)
SELECT
    'user:' || id,
    ROW_NUMBER() OVER (PARTITION BY id ORDER BY created_at),
    jsonb_build_object('Case', 'UsernameChanged', 'userId', id, 'username', username),
    created_at
FROM "user"
ON CONFLICT (stream_id, stream_version) DO NOTHING;

-- 2. MessageReceived — from message table (always version 1 per stream)
INSERT INTO event(stream_id, stream_version, data, created_at)
SELECT
    'message:' || chat_id || ':' || message_id,
    1,
    jsonb_build_object('Case', 'MessageReceived', 'chatId', chat_id, 'messageId', message_id,
                       'userId', user_id, 'text', text, 'rawMessage', raw_message::jsonb),
    created_at
FROM message
ON CONFLICT (stream_id, stream_version) DO NOTHING;

-- 3. UserBanned — union manual bans + bot bans, assign version per user ordered by time
-- Both sources share the same user: stream so versions MUST be assigned together via a UNION
-- (doing them separately would produce version collisions for users banned by both)
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
           -- offset by 1 because UsernameChanged is always version 1 (user table has one row per user)
           1 + ROW_NUMBER() OVER (PARTITION BY user_id ORDER BY banned_at) AS stream_ver
    FROM all_bans
)
INSERT INTO event(stream_id, stream_version, data, created_at)
SELECT
    'user:' || user_id,
    stream_ver,
    jsonb_build_object(
        'Case', 'UserBanned',
        'userId', user_id,
        'bannedBy', CASE
            WHEN is_bot_ban THEN jsonb_build_object('Case', 'BannedByAutoBan', 'chatId', banned_in_chat_id, 'messageText', message_text)
            ELSE jsonb_build_object('Case', 'BannedByVahter', 'vahterId', banned_by, 'chatId', COALESCE(banned_in_chat_id, 0), 'messageId', COALESCE(message_id, 0), 'messageText', message_text)
        END
    ),
    banned_at
FROM numbered
ON CONFLICT (stream_id, stream_version) DO NOTHING;

-- 4. MessageMarkedSpam — from false_negative_messages (version 2, MessageReceived is always 1)
INSERT INTO event(stream_id, stream_version, data, created_at)
SELECT
    'message:' || chat_id || ':' || message_id,
    2,
    jsonb_build_object('Case', 'MessageMarkedSpam', 'chatId', chat_id, 'messageId', message_id, 'markedBy', NULL),
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
-- Use dynamic version to avoid collision with MessageMarkedSpam (both were hardcoded to 2)
INSERT INTO event(stream_id, stream_version, data, created_at)
SELECT
    'message:' || m.chat_id || ':' || m.message_id,
    COALESCE((SELECT MAX(stream_version) FROM event e
              WHERE e.stream_id = 'message:' || m.chat_id || ':' || m.message_id), 1) + 1,
    jsonb_build_object('Case', 'MessageMarkedHam', 'chatId', m.chat_id, 'messageId', m.message_id, 'text', fp.text, 'markedBy', NULL),
    NOW()
FROM false_positive_messages fp
JOIN msg_hash_lookup m ON m.text_hash = fp.text_hash
ON CONFLICT (stream_id, stream_version) DO NOTHING;

DROP TABLE msg_hash_lookup;

-- 6. VahterActed — from vahter_actions (always version 1 per moderation stream)
INSERT INTO event(stream_id, stream_version, data, created_at)
SELECT
    'moderation:' || target_chat_id || ':' || target_message_id,
    1,
    jsonb_build_object('Case', 'VahterActed', 'vahterId', vahter_id, 'actionType',
                       CASE action_type
                           WHEN 'potential_kill' THEN 'PotentialKill'
                           WHEN 'manual_ban' THEN 'ManualBan'
                           WHEN 'potential_soft_spam' THEN 'PotentialSoftSpam'
                           WHEN 'potential_not_spam' THEN 'PotentialNotSpam'
                           WHEN 'detected_not_spam' THEN 'DetectedNotSpam'
                           ELSE action_type
                       END,
                       'targetUserId', target_user_id, 'chatId', target_chat_id, 'messageId', target_message_id),
    created_at
FROM vahter_actions
ON CONFLICT (stream_id, stream_version) DO NOTHING;

-- 7. LlmClassified — from llm_triage
INSERT INTO event(stream_id, stream_version, data, created_at)
SELECT
    'detection:' || chat_id || ':' || message_id,
    1,
    jsonb_build_object('Case', 'LlmClassified', 'chatId', chat_id, 'messageId', message_id, 'verdict', verdict,
                       'promptTokens', prompt_tokens, 'completionTokens', completion_tokens, 'latencyMs', latency_ms),
    created_at
FROM llm_triage
ON CONFLICT (stream_id, stream_version) DO NOTHING;

-- Partial indexes for JSONB queries on the event table
CREATE INDEX IF NOT EXISTS idx_event_msg_received_user
    ON event(((data->>'userId')::BIGINT), created_at)
    WHERE event_type = 'MessageReceived';

CREATE INDEX IF NOT EXISTS idx_event_bot_auto_deleted_user
    ON event(((data->>'userId')::BIGINT))
    WHERE event_type = 'BotAutoDeleted';

CREATE INDEX IF NOT EXISTS idx_event_msg_marked_ham_text
    ON event((data->>'text'))
    WHERE event_type = 'MessageMarkedHam';

CREATE INDEX IF NOT EXISTS idx_event_vahter_acted_vahter
    ON event(((data->>'vahterId')::BIGINT), created_at)
    WHERE event_type = 'VahterActed';

CREATE INDEX IF NOT EXISTS idx_event_username_changed
    ON event(stream_id, id DESC)
    WHERE event_type = 'UsernameChanged';
