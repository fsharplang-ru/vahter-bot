-- Adds the temporary "vetted" spam-protection window's fields to the snapshot_user read model
-- (see Types.fs's User.SpamProtectionUntil/SpamProtectionHits and Bot.fs's EnforceOrDemote /
-- MaybeGrantSpamProtection). Schema-only — no bot_setting rows are seeded here (SPAM_PROTECTION_*
-- values are hand-run SQL, per AGENTS.md's Settings configuration convention).
--
-- Same style as V38__snapshot.sql: GENERATED STORED columns projected from the JSONB `state`
-- blob, reusing the IMMUTABLE jsonb_utc_timestamptz wrapper that migration already defined.

ALTER TABLE snapshot_user
    ADD COLUMN spam_protection_until TIMESTAMPTZ GENERATED ALWAYS AS (jsonb_utc_timestamptz(state->>'spamProtectionUntil')) STORED,
    ADD COLUMN spam_protection_hits  INT         GENERATED ALWAYS AS ((state->>'spamProtectionHits')::int)                STORED;

-- Supports "who's currently protected" debug/ops queries without a full table scan.
CREATE INDEX idx_snapshot_user_spam_protection ON snapshot_user (spam_protection_until) WHERE spam_protection_until IS NOT NULL;
