-- Backfill bannedAt into existing UserBanned events from the row's created_at.
UPDATE event
SET data = data || jsonb_build_object('bannedAt', to_char(created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'))
WHERE event_type = 'UserBanned'
  AND data->>'bannedAt' IS NULL;
