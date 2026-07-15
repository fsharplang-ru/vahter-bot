-- Read-model snapshot tables holding the latest folded state per stream.
-- Written atomically in the same TX as the event append (see DB.fs upsert helpers),
-- so they never drift from the event log. Purely a read model / debugging aid here;
-- the event table remains the source of truth.

-- IMMUTABLE wrapper so a timestamptz value can be used in a GENERATED column.
-- A bare `text::timestamptz` is only STABLE (the planner assumes it depends on the
-- TimeZone GUC) and Postgres rejects STABLE expressions in generated columns. This is
-- safe because the server runs UTC-only and the serialized values carry a `Z` offset,
-- making the cast deterministic regardless of session TimeZone.
CREATE OR REPLACE FUNCTION jsonb_utc_timestamptz(txt text) RETURNS timestamptz
    LANGUAGE sql IMMUTABLE PARALLEL SAFE RETURNS NULL ON NULL INPUT AS
$$ SELECT txt::timestamptz $$;

-- Latest state of the user:* stream.
CREATE TABLE IF NOT EXISTS snapshot_user (
    user_id        BIGINT      PRIMARY KEY,
    stream_version INT         NOT NULL,
    state          JSONB       NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL,                 -- first event's created_at (sourced in upsert)
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),   -- last snapshot write
    username       TEXT        GENERATED ALWAYS AS (state->>'username')                        STORED,
    banned         BOOLEAN     GENERATED ALWAYS AS ((state->>'banned')::boolean)               STORED,
    banned_at      TIMESTAMPTZ GENERATED ALWAYS AS (jsonb_utc_timestamptz(state->>'bannedAt')) STORED,
    banned_by      BIGINT      GENERATED ALWAYS AS ((state->>'bannedByUserId')::bigint)        STORED,
    reaction_count INT         GENERATED ALWAYS AS ((state->>'reactionCount')::int)            STORED
);
CREATE INDEX idx_snapshot_user_username ON snapshot_user (lower(username));
CREATE INDEX idx_snapshot_user_banned   ON snapshot_user (banned_at) WHERE banned;

-- Latest state of a message, keyed by (chat_id, message_id). Two JSONB columns, each
-- owned by one stream: message_data (message:*) and moderation_data (moderation:*).
-- Either may be NULL until its first event; the *_version guards keep each column's
-- upsert monotonic and independent, so the two streams can land in any order.
CREATE TABLE IF NOT EXISTS snapshot_message (
    chat_id         BIGINT      NOT NULL,
    message_id      INT         NOT NULL,
    message_data    JSONB,                              -- folded Message state (message:* stream)
    moderation_data JSONB,                              -- folded Moderation state (moderation:* stream)
    msg_version     INT,                                -- message:* stream_version
    mod_version     INT,                                -- moderation:* stream_version
    created_at      TIMESTAMPTZ,                        -- message receipt time (set by message path)
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    -- from message_data:
    user_id     BIGINT  GENERATED ALWAYS AS ((message_data->>'userId')::bigint)   STORED,
    text        TEXT    GENERATED ALWAYS AS (message_data->>'text')               STORED,
    spam_status TEXT    GENERATED ALWAYS AS (message_data->>'classification')     STORED,  -- 'Unknown'|'Spam'|'Ham'
    deleted     BOOLEAN GENERATED ALWAYS AS ((message_data->>'deleted')::boolean) STORED,
    -- from moderation_data:
    bot_auto_deleted BOOLEAN GENERATED ALWAYS AS ((moderation_data->>'botAutoDeleted')::boolean) STORED,
    vahter_verdict   TEXT    GENERATED ALWAYS AS (moderation_data->>'verdict')                    STORED,  -- 'Spam'|'NotSpam'|null
    PRIMARY KEY (chat_id, message_id)
);
CREATE INDEX idx_snapshot_message_user      ON snapshot_message (user_id);
CREATE INDEX idx_snapshot_message_status    ON snapshot_message (spam_status);
CREATE INDEX idx_snapshot_message_verdict   ON snapshot_message (vahter_verdict) WHERE vahter_verdict IS NOT NULL;
-- supports MlData (created_at >= criticalDate) and GetUserStats (per-user, recent-first):
CREATE INDEX idx_snapshot_message_user_time ON snapshot_message (user_id, created_at DESC);

GRANT SELECT, INSERT, UPDATE ON snapshot_user, snapshot_message TO vahter_bot_ban_service;
