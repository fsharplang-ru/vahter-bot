ALTER TABLE join_deletion_queue ADD COLUMN single_only boolean NOT NULL DEFAULT false;

CREATE INDEX join_deletion_chat_ready ON join_deletion_queue (chat_id, single_only, available_at, message_id)
    WHERE available_at IS NOT NULL;

INSERT INTO bot_setting (key, value, type, feature_group, description) VALUES
    ('JOIN_DELETE_BULK_THRESHOLD', '2', 'FREE_FORM', 'MODERATION', 'Use deleteMessages when the available same-chat batch exceeds this many IDs (1 to 100).'),
    ('JOIN_DELETE_MAX_CLAIM', '100', 'FREE_FORM', 'MODERATION', 'Maximum IDs per adaptive same-chat claim (1 to 100).')
ON CONFLICT (key) DO NOTHING;
