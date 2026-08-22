-- Partial index for prompt v2's repetition signal (LlmTriage.fs/DB.fs GetTextRepetition);
-- replaces the global md5 index V33 dropped. PLAIN transactional CREATE INDEX IF NOT EXISTS, not
-- CONCURRENTLY — CIC inside Flyway deadlocks, see issue #407. PRODUCTION pre-creates this exact
-- index by hand via CONCURRENTLY before merge (same name/definition, byte-identical), so IF NOT EXISTS no-ops here.
CREATE INDEX IF NOT EXISTS idx_event_msg_text_md5_created_at
    ON event (msg_text_md5, created_at)
    WHERE event_type = 'MessageReceived' AND msg_text_md5 IS NOT NULL;
