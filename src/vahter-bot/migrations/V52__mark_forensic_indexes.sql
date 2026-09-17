-- Remaining debug-session indexes (V30, V38, V43, V48) get the idx_forensic_ prefix so the
-- catalog itself shows which indexes no bot query depends on.
ALTER INDEX IF EXISTS idx_event_username_lower           RENAME TO idx_forensic_event_username_lower;
ALTER INDEX IF EXISTS idx_event_user_ban_by_stream       RENAME TO idx_forensic_event_user_ban_by_stream;
ALTER INDEX IF EXISTS idx_event_mlscored_score_created_at RENAME TO idx_forensic_event_mlscored_score_created_at;
ALTER INDEX IF EXISTS idx_snapshot_user_username         RENAME TO idx_forensic_snapshot_user_username;
ALTER INDEX IF EXISTS idx_snapshot_user_banned           RENAME TO idx_forensic_snapshot_user_banned;
ALTER INDEX IF EXISTS idx_snapshot_user_spam_protection  RENAME TO idx_forensic_snapshot_user_spam_protection;
