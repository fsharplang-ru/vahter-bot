-- Debug views to restore table-like ergonomics after event sourcing migration.
-- All objects here are read-only (VIEWs) — no changes to event data.
-- Indexes used by these views are in V30__debug_indexes.sql.

-- ============================================================
-- VIEW: v_users
-- Current username + ban status per user.
-- Replaces the old mutable `user` table for debugging.
--
-- Usage:
--   SELECT * FROM v_users WHERE username = 'someuser';
--   SELECT * FROM v_users WHERE user_id = 123456789;
--   SELECT * FROM v_users WHERE is_banned AND username LIKE 'test%';
-- ============================================================
CREATE VIEW v_users AS
WITH latest_username AS (
    -- Uses idx_event_username_changed (stream_id, id DESC) WHERE UsernameChanged
    SELECT DISTINCT ON (stream_id)
        replace(stream_id, 'user:', '')::bigint AS user_id,
        lower(data->>'username')               AS username,
        created_at                             AS username_updated_at
    FROM event
    WHERE event_type = 'UsernameChanged'
    ORDER BY stream_id, id DESC
),
latest_ban_event AS (
    -- Uses idx_event_user_ban_by_stream (stream_id, created_at DESC)
    SELECT DISTINCT ON (stream_id)
        replace(stream_id, 'user:', '')::bigint                            AS user_id,
        event_type,
        created_at                                                         AS ban_event_at,
        COALESCE(data->'bannedBy'->>'Case', data->'actor'->>'Case')        AS ban_actor_raw
    FROM event
    WHERE event_type = 'UserBanned' OR event_type = 'UserUnbanned'
    ORDER BY stream_id, created_at DESC
)
SELECT
    COALESCE(u.user_id, b.user_id)  AS user_id,
    u.username,
    u.username_updated_at,
    (b.event_type = 'UserBanned')   AS is_banned,
    b.ban_event_at,
    CASE b.ban_actor_raw
        -- legacy bannedBy cases (backfilled from pre-event-sourcing data)
        WHEN 'BannedByVahter'  THEN 'manual'
        WHEN 'BannedByAutoBan' THEN 'autoban'
        WHEN 'BannedByAI'      THEN 'ai'
        -- modern actor cases
        WHEN 'User'            THEN 'manual'
        WHEN 'Bot'             THEN 'bot'
        WHEN 'ML'              THEN 'autoban'
        WHEN 'LLM'             THEN 'llm'
        ELSE b.ban_actor_raw
    END AS ban_reason
FROM latest_username u
FULL OUTER JOIN latest_ban_event b ON u.user_id = b.user_id;

-- ============================================================
-- VIEW: v_recent_bans
-- Latest bans with human-readable context.
-- Replaces browsing the old `banned` table.
--
-- Usage:
--   SELECT * FROM v_recent_bans LIMIT 20;
--   SELECT * FROM v_recent_bans WHERE ban_type = 'manual';
--   SELECT * FROM v_recent_bans WHERE username = 'someuser';
-- ============================================================
CREATE VIEW v_recent_bans AS
SELECT
    b.created_at                                                        AS banned_at,
    (b.data->>'userId')::bigint                                         AS user_id,
    -- Lateral: uses idx_event_username_changed (stream_id, id DESC)
    (SELECT lower(data->>'username')
     FROM event
     WHERE event_type = 'UsernameChanged'
       AND stream_id = b.stream_id
     ORDER BY id DESC
     LIMIT 1)                                                           AS username,
    CASE COALESCE(b.data->'bannedBy'->>'Case', b.data->'actor'->>'Case')
        -- legacy bannedBy cases (backfilled from pre-event-sourcing data)
        WHEN 'BannedByVahter'  THEN 'manual'
        WHEN 'BannedByAutoBan' THEN 'autoban'
        WHEN 'BannedByAI'      THEN 'ai'
        -- modern actor cases
        WHEN 'User'            THEN 'manual'
        WHEN 'Bot'             THEN 'bot'
        WHEN 'ML'              THEN 'autoban'
        WHEN 'LLM'             THEN 'llm'
        ELSE 'unknown'
    END                                                                 AS ban_type,
    COALESCE(
        b.data->'bannedBy'->>'messageText',
        b.data->>'messageText'
    )                                                                   AS message_text
FROM event b
WHERE b.event_type = 'UserBanned'
ORDER BY b.created_at DESC;

-- ============================================================
-- VIEW: v_messages
-- Raw message lookup for spam pattern investigation.
-- Use when you have a userId, chatId, or time window and need
-- to inspect what the bot actually received (rawMessage JSON).
--
-- Usage:
--   SELECT text, raw_message FROM v_messages WHERE user_id = 123456 LIMIT 20;
--   SELECT * FROM v_messages WHERE chat_id = -1002382253925 AND created_at > now() - interval '1 hour';
--   SELECT raw_message FROM v_messages WHERE chat_id = -1002382253925 AND message_id = 364823;
-- ============================================================
CREATE VIEW v_messages AS
SELECT
    (data->>'chatId')::bigint   AS chat_id,
    (data->>'messageId')::int   AS message_id,
    (data->>'userId')::bigint   AS user_id,
    data->>'text'               AS text,
    data->>'rawMessage'         AS raw_message,
    created_at
FROM event
WHERE event_type = 'MessageReceived'
ORDER BY created_at DESC;

-- ============================================================
-- VIEW: v_recent_actions
-- All recent moderation events in one place:
-- vahter kills, bot auto-deletes, LLM verdicts, ML scores, spam marks.
--
-- Usage:
--   SELECT * FROM v_recent_actions ORDER BY created_at DESC LIMIT 30;
--   SELECT * FROM v_recent_actions WHERE user_id = 123456789;
--   SELECT * FROM v_recent_actions WHERE event_type = 'LlmClassified' AND llm_verdict = 'KILL';
-- ============================================================
CREATE VIEW v_recent_actions AS
SELECT
    e.created_at,
    e.event_type,
    (e.data->>'chatId')::bigint                                                   AS chat_id,
    (e.data->>'messageId')::int                                                   AS message_id,
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
