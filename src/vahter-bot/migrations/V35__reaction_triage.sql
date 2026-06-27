-- Reaction-spam triage: per-user profile cache + cooldown column + feature flags.
-- The triage pipeline is gated by REACTION_SPAM_ENABLED (already exists, V18__reactions.sql)
-- and LLM_REACTION_TRIAGE_AUTO_ACT (added below — defaults to false / shadow mode).

CREATE TABLE user_profile_cache (
    user_id      BIGINT      PRIMARY KEY,
    photo_bytes  BYTEA,
    bio          TEXT,
    fetched_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

GRANT SELECT, INSERT, UPDATE ON user_profile_cache TO vahter_bot_ban_service;

-- Cooldown is event-sourced: ReactionTriageNotSpamSet event on the user:{userId} stream
-- carries the until-timestamp. The User aggregate folds it into NotSpamUntil. No DDL needed.

INSERT INTO bot_setting (key, value, type, feature_group, description) VALUES
    ('LLM_REACTION_TRIAGE_AUTO_ACT', 'false', 'FEATURE_FLAG', 'REACTION_SPAM',
     'When true, LLM verdict acts autonomously (UNSURE falls through to vahter). When false (shadow mode), LLM runs but verdict is recorded only — vahter always decides.'),
    ('LLM_REACTION_TRIAGE_SHADOW_DISABLE', 'false', 'FEATURE_FLAG', 'REACTION_SPAM',
     'Escape hatch: if true, skip the LLM call entirely (do not even shadow). Normally false.'),
    ('REACTION_NOT_SPAM_COOLDOWN_DAYS', '30', 'FREE_FORM', 'REACTION_SPAM',
     'Days after vahter/LLM verdict NOT_SPAM before re-triaging the same user.');
