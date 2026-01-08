DROP INDEX IF EXISTS idx_false_positive_messages_text;

ALTER TABLE false_positive_messages
    ADD COLUMN text_hash uuid GENERATED ALWAYS AS (md5(text)::uuid) STORED;

CREATE UNIQUE INDEX idx_messages_hash ON public.false_positive_messages (text_hash);

-- we trimmed some messages to 3000 chars in the past, so we need to delete those entries
DELETE FROM false_positive_messages
WHERE length(text) = 3000;
