-- Verdict cache so identical spam (same text / same reaction-spammer) is classified by the
-- LLM once instead of re-asking Azure on every duplicate. This collapses the request bursts
-- that were tripping Azure's per-minute rate limit (HTTP 429), which in turn leaked
-- duplicates into the vahter action channel instead of auto-handling them.
--
-- cache_key convention:
--   text triage     : 'text:' || md5(message text)
--   reaction triage : 'reaction:' || user_id
-- Freshness is enforced at read time via the configurable LLM_VERDICT_CACHE_TTL_MINUTES.

CREATE TABLE llm_verdict_cache (
    cache_key  TEXT        PRIMARY KEY,
    verdict    TEXT        NOT NULL,
    reason     TEXT,
    model_name TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

GRANT SELECT, INSERT, UPDATE ON llm_verdict_cache TO vahter_bot_ban_service;

INSERT INTO bot_setting (key, value, type, feature_group, description) VALUES
    ('LLM_VERDICT_CACHE_TTL_MINUTES', '60', 'FREE_FORM', 'LLM',
     'Minutes a cached LLM verdict (text or reaction triage) is reused before re-asking the model. Dedups identical spam across channels and absorbs rate-limit bursts.');
