CREATE TABLE join_history_check (
    chat_id bigint NOT NULL,
    minute timestamptz NOT NULL,
    notices integer NOT NULL,
    first_id integer NOT NULL,
    last_id integer NOT NULL,
    next_id bigint NOT NULL,
    due_at timestamptz NOT NULL,
    failures integer NOT NULL DEFAULT 0,
    PRIMARY KEY (chat_id, minute)
);
CREATE INDEX join_history_due ON join_history_check (due_at)
    WHERE notices >= 32 AND failures < 3 AND next_id <= last_id + 100::bigint;
GRANT SELECT, INSERT, UPDATE, DELETE ON join_history_check TO vahter_bot_ban_service;

CREATE TABLE join_history_cooldown (id integer PRIMARY KEY CHECK (id = 1), until_at timestamptz NOT NULL);
GRANT SELECT, INSERT, UPDATE ON join_history_cooldown TO vahter_bot_ban_service;
