CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

CREATE TABLE callback
(
    internal_id BIGSERIAL PRIMARY KEY,
    id          UUID        NOT NULL DEFAULT uuid_generate_v4(),
    data        JSONB       NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT timezone('utc'::TEXT, NOW())
);

CREATE INDEX idx_callback_data ON callback USING GIN (data);
CREATE UNIQUE INDEX idx_callback_id ON callback (id);

ALTER TABLE false_positive_messages
    ADD COLUMN text TEXT NULL;

UPDATE false_positive_messages
SET text = b.message_text
FROM (SELECT b.message_text, b.id
      FROM false_positive_messages fpm
               JOIN public.banned b ON b.id = fpm.id) AS b
WHERE false_positive_messages.id = b.id;

ALTER TABLE false_positive_messages
    ALTER COLUMN text SET NOT NULL;

CREATE UNIQUE INDEX idx_false_positive_messages_text
    ON false_positive_messages (text);

ALTER TABLE false_positive_messages
    DROP COLUMN id;
