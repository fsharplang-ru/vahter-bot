ALTER TABLE message
    ADD COLUMN text        TEXT,
    ADD COLUMN raw_message JSONB;

CREATE INDEX message_raw_message_idx ON message USING GIN (raw_message);
