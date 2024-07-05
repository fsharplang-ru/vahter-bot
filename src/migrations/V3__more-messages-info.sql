CREATE TABLE banned
(
    id                      BIGSERIAL PRIMARY KEY,
    message_id              INTEGER   NULL,
    message_text            TEXT      NOT NULL,
    banned_user_id          BIGINT    NOT NULL
        REFERENCES "user" (id),
    banned_at               TIMESTAMP NOT NULL,
    banned_in_chat_id       BIGINT    NULL,
    banned_in_chat_username TEXT      NULL,
    banned_by               BIGINT    NOT NULL
        REFERENCES "user" (id)
);

CREATE INDEX banned_banned_user_id_idx
    ON banned (banned_user_id);

CREATE INDEX banned_banned_by_idx
    ON banned (banned_by);

CREATE INDEX banned_banned_in_chat_id_idx
    ON banned (banned_in_chat_id);

CREATE INDEX banned_message_id_idx
    ON banned (message_id);

INSERT INTO banned (message_text, banned_user_id, banned_at, banned_by, message_id, banned_in_chat_id, banned_in_chat_username)
SELECT b.ban_reason
     , b.id
     , b.banned_at
     , vahter.id
     , NULL -- don't have it
     , NULL -- don't have it
     , NULL -- don't have it
FROM "user" b
         JOIN "user" vahter ON vahter.id = b.banned_by
WHERE b.ban_reason IS NOT NULL;
