-- Postgres-backed chat-admin snapshot, shared across pods — one pod refreshes under the
-- 'chat_admins_refresh' scheduled_job lease; every pod reloads its local copy on a timer.
CREATE TABLE chat_admin (
    chat_id    BIGINT      NOT NULL,
    user_id    BIGINT      NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (chat_id, user_id)
);

GRANT SELECT, INSERT, UPDATE, DELETE ON chat_admin TO vahter_bot_ban_service;

INSERT INTO scheduled_job (job_name) VALUES ('chat_admins_refresh');
