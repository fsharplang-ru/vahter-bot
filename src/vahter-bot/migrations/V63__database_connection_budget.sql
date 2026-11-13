INSERT INTO bot_setting (key, value, type, feature_group, description)
VALUES ('DB_MAX_POOL_SIZE', '3', 'FREE_FORM', 'DATABASE', 'Maximum pooled database connections per bot process. Takes effect on restart, capped by DATABASE_URL.')
ON CONFLICT (key) DO NOTHING;
