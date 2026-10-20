INSERT INTO bot_setting (key, value, type, feature_group, description)
VALUES ('DELETE_JOIN_MESSAGES_IN_CHATS', '[]', 'JSON_BLOB', 'MODERATION',
        'JSON array of monitored chat IDs whose member join service messages are deleted; empty disables deletion.')
ON CONFLICT (key) DO NOTHING;
