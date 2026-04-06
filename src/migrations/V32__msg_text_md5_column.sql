-- Add stored generated column for text md5 hash and rebuild the index on it.
-- NOTE: ADD COLUMN GENERATED STORED rewrites the table — run during low traffic.

ALTER TABLE event ADD COLUMN IF NOT EXISTS msg_text_md5 TEXT
    GENERATED ALWAYS AS (md5(data->>'text')) STORED;

-- Replace the md5 expression index with one on the stored column (enables Index Only Scan)
DROP INDEX IF EXISTS idx_event_msg_received_user_text;

CREATE INDEX IF NOT EXISTS idx_event_msg_received_user_text
    ON event(((data->>'userId')::BIGINT), msg_text_md5)
    WHERE event_type = 'MessageReceived';
