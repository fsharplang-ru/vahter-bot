-- Partial index on MessageReceived so per-chat latest-message lookups (DISTINCT ON chatId, created_at DESC)
-- hit an index instead of a full scan; only MessageReceived rows pay for it.
DROP INDEX CONCURRENTLY IF EXISTS idx_event_msgreceived_chat_created_at;
CREATE INDEX CONCURRENTLY idx_event_msgreceived_chat_created_at
    ON event (((data->>'chatId')::bigint), created_at DESC)
    WHERE event_type = 'MessageReceived';
