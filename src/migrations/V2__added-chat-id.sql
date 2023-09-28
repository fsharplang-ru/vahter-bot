DROP TABLE message;

CREATE TABLE "message"
(
    id         BIGSERIAL   NOT NULL PRIMARY KEY,
    chat_id    BIGINT      NOT NULL,
    message_id INT         NOT NULL,
    user_id    BIGINT      NOT NULL
        CONSTRAINT message_user_id_fkey
            REFERENCES "user" (id)
            ON DELETE CASCADE,

    created_at TIMESTAMPTZ NOT NULL DEFAULT timezone('utc'::TEXT, NOW())
);

CREATE UNIQUE INDEX message_chat_id_message_id_uindex
    ON "message" (chat_id, message_id);

CREATE INDEX message_user_id_index
    ON "message" (user_id);
