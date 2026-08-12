-- Deprecate (rename only) the 10 pre-event-sourcing tables superseded by the
-- append-only `event` table (V23__events.sql, cut over 2026-04-02). See issue #330
-- for the full audit: nothing writes to these tables any more, and application code
-- no longer reads from them either. This migration does NOT drop any table — it
-- only renames them with a `deprecated_` prefix so they stay in place while step 3
-- (observe) runs; dropping them is a separate, later, owner-run step.
-- Idempotent: ALTER TABLE IF EXISTS is a no-op if already renamed / already absent.

ALTER TABLE IF EXISTS banned                    RENAME TO deprecated_banned;
ALTER TABLE IF EXISTS banned_by_bot             RENAME TO deprecated_banned_by_bot;
ALTER TABLE IF EXISTS vahter_actions            RENAME TO deprecated_vahter_actions;
ALTER TABLE IF EXISTS message                   RENAME TO deprecated_message;
ALTER TABLE IF EXISTS "user"                    RENAME TO deprecated_user;
ALTER TABLE IF EXISTS callback                  RENAME TO deprecated_callback;
ALTER TABLE IF EXISTS llm_triage                RENAME TO deprecated_llm_triage;
ALTER TABLE IF EXISTS false_positive_messages   RENAME TO deprecated_false_positive_messages;
ALTER TABLE IF EXISTS false_negative_messages   RENAME TO deprecated_false_negative_messages;
ALTER TABLE IF EXISTS false_positive_users      RENAME TO deprecated_false_positive_users;
