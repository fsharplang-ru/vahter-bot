-- Username text search (case-insensitive via lower()).
-- Enables: WHERE username = 'someuser' in v_users / v_recent_bans.
CREATE INDEX IF NOT EXISTS idx_event_username_lower
    ON event (lower(data->>'username'))
    WHERE event_type = 'UsernameChanged';

-- Ban status lookup per user stream.
-- Enables DISTINCT ON (stream_id) ORDER BY stream_id, created_at DESC
-- without a full seq scan (previously hitting ~420k rows).
CREATE INDEX IF NOT EXISTS idx_event_user_ban_by_stream
    ON event (stream_id, created_at DESC)
    WHERE event_type = 'UserBanned' OR event_type = 'UserUnbanned';
