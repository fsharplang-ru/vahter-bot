-- The index idx_event_msg_received_user_text was created manually but is INVALID.
-- It failed because data->>'text' can exceed btree's max row size (~2712 bytes).
-- Fix: index on md5(text) instead (always 32 chars).
DROP INDEX IF EXISTS idx_event_msg_received_user_text;

CREATE INDEX IF NOT EXISTS idx_event_msg_received_user_text
    ON event(((data->>'userId')::BIGINT), (md5(data->>'text')))
    WHERE event_type = 'MessageReceived';
