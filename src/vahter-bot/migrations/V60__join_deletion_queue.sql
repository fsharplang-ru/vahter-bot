CREATE TABLE join_deletion_queue (
    chat_id bigint NOT NULL,
    message_id bigint NOT NULL,
    enqueued_at timestamptz NOT NULL DEFAULT now(),
    available_at timestamptz DEFAULT now(),
    attempt integer NOT NULL DEFAULT 0,
    flood_until timestamptz,
    PRIMARY KEY (chat_id, message_id)
);

CREATE INDEX join_deletion_ready ON join_deletion_queue (available_at) WHERE available_at IS NOT NULL;
CREATE INDEX join_deletion_flood ON join_deletion_queue (flood_until) WHERE flood_until IS NOT NULL;
GRANT SELECT, INSERT, UPDATE, DELETE ON join_deletion_queue TO vahter_bot_ban_service;
