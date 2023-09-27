CREATE TABLE "user"
(
    id         BIGINT      NOT NULL
        PRIMARY KEY,
    username   TEXT        NULL,

    banned_by  BIGINT      NULL
        CONSTRAINT user_banned_by_fkey
            REFERENCES "user" (id),
    banned_at  TIMESTAMPTZ NULL,
    ban_reason TEXT        NULL,

    created_at TIMESTAMPTZ NOT NULL DEFAULT timezone('utc'::TEXT, NOW()),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT timezone('utc'::TEXT, NOW())
);

CREATE UNIQUE INDEX "user_username_uindex"
    ON "user" (username);

CREATE INDEX "user_banned_by_index"
    ON "user" (banned_by);

CREATE TABLE "message"
(
    id         INT         NOT NULL PRIMARY KEY,
    user_id    BIGINT      NOT NULL
        CONSTRAINT message_user_id_fkey
            REFERENCES "user" (id)
            ON DELETE CASCADE,

    created_at TIMESTAMPTZ NOT NULL DEFAULT timezone('utc'::TEXT, NOW())
);

CREATE INDEX "message_user_id_index"
    ON "message" (user_id);
