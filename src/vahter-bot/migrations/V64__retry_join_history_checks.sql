DROP INDEX join_history_due;
CREATE INDEX join_history_due ON join_history_check (due_at)
    WHERE notices >= 32 AND next_id <= last_id + 100::bigint;

INSERT INTO bot_setting (key, value, type, feature_group, description) VALUES
    ('JOIN_HISTORY_REQUEST_TIMEOUT_SECONDS', '30', 'FREE_FORM', 'MODERATION', 'Timeout per MTProto history call, including login and peer resolution (1 to 120 seconds).'),
    ('JOIN_HISTORY_RETRY_MAX_SECONDS', '900', 'FREE_FORM', 'MODERATION', 'Maximum exponential backoff for failed history pages (60 to 3600 seconds).')
ON CONFLICT (key) DO NOTHING;
