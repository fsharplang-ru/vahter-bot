-- Issue #330 step 4: drop the 10 tables V41 renamed to deprecated_* (#362).
-- No CASCADE — an unexpected external dependent fails this loudly instead.

DROP TABLE IF EXISTS deprecated_banned, deprecated_banned_by_bot, deprecated_vahter_actions, deprecated_message, deprecated_user, deprecated_callback, deprecated_llm_triage, deprecated_false_positive_messages, deprecated_false_negative_messages, deprecated_false_positive_users;
