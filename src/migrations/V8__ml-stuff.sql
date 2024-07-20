CREATE TABLE banned_by_bot
(
    id                      BIGSERIAL PRIMARY KEY,
    message_id              INTEGER     NULL,
    message_text            TEXT,
    banned_user_id          BIGINT      NOT NULL
        REFERENCES "user" (id),
    banned_at               TIMESTAMPTZ NOT NULL,
    banned_in_chat_id       BIGINT      NULL,
    banned_in_chat_username TEXT        NULL
);

CREATE INDEX banned_by_bot_banned_user_id_idx
    ON banned_by_bot (banned_user_id);

CREATE INDEX banned_by_bot_banned_in_chat_id_idx
    ON banned_by_bot (banned_in_chat_id);

CREATE INDEX banned_by_bot_message_id_idx
    ON banned_by_bot (message_id);
