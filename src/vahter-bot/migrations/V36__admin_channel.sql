-- Admin (vahter) channel for privileged /vahter commands.
-- value is left NULL on purpose; ops set the real channel id via bot_setting.
-- The bot reads this with a "0" (disabled) fallback, so a NULL value is safe.
INSERT INTO bot_setting (key, value, type, feature_group, description) VALUES
    ('ADMIN_CHANNEL_ID', NULL, 'FREE_FORM', 'CHANNELS',
     'Telegram chat id of the private vahter moderators channel where /vahter admin commands are accepted. 0 or unset disables admin commands.')
ON CONFLICT (key) DO NOTHING;
