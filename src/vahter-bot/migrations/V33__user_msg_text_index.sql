-- Per-user distinct message-text projection.
-- Replaces the slow `COUNT(DISTINCT msg_text_md5) FROM event WHERE userId=X`
-- query, which planned as Bitmap Heap Scan and took up to 5s on heavy users
-- because the event table's visibility map is rarely all-visible.
-- One row per (user, distinct text-md5) seen — read becomes a tight PK range scan.

CREATE TABLE IF NOT EXISTS user_msg_text_index (
    user_id      BIGINT NOT NULL,
    msg_text_md5 TEXT   NOT NULL,
    PRIMARY KEY (user_id, msg_text_md5)
);

GRANT SELECT, INSERT, UPDATE, DELETE ON user_msg_text_index TO vahter_bot_ban_service;

-- Backfill from existing event log. ON CONFLICT guards against any race with
-- live traffic landing during the deploy window.
INSERT INTO user_msg_text_index (user_id, msg_text_md5)
SELECT DISTINCT (data->>'userId')::BIGINT, msg_text_md5
FROM event
WHERE event_type = 'MessageReceived'
  AND msg_text_md5 IS NOT NULL
ON CONFLICT DO NOTHING;

-- The covering partial index on (userId, msg_text_md5) is no longer used by
-- any query — drop it to reclaim space.
DROP INDEX IF EXISTS idx_event_msg_received_user_text;
