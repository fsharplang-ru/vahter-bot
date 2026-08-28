-- Pre-created CONCURRENTLY on prod by hand (CIC deadlocks in Flyway); these statements no-op there.
-- For ad-hoc forensic queries over event.data->>'rawMessage', not app paths.
CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE INDEX IF NOT EXISTS idx_event_msgreceived_msgid_chatid
  ON event ((((data->>'messageId')::bigint)), (((data->>'chatId')::bigint)))
  WHERE event_type = 'MessageReceived';

CREATE INDEX IF NOT EXISTS idx_event_rawmessage_trgm
  ON event USING gin ((data->>'rawMessage') gin_trgm_ops)
  WHERE event_type = 'MessageReceived';
