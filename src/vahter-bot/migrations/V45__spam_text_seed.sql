-- Postgres-backed spam-text cache, shared across pods — a single-pod ConcurrentDictionary
-- can't see a /ban handled by a different pod. TTL via expires_at, swept by Cleanup.fs.
CREATE TABLE spam_text_seed (
    normalized_text TEXT        PRIMARY KEY,
    chat_id         BIGINT      NOT NULL,
    message_id      BIGINT      NOT NULL,
    seeded_at       TIMESTAMPTZ NOT NULL,
    expires_at      TIMESTAMPTZ NOT NULL
);

CREATE INDEX idx_spam_text_seed_expires_at ON spam_text_seed (expires_at);

GRANT SELECT, INSERT, UPDATE, DELETE ON spam_text_seed TO vahter_bot_ban_service;
