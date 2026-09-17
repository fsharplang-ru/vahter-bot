-- idx_forensic_*: debug-session indexes, not read by bot code. GIN ones are built with fastupdate
-- off, since a pending-list flush stalls the inserting webhook request for 10-30s on this tier.
DROP INDEX CONCURRENTLY IF EXISTS idx_event_rawmessage_trgm;
DROP INDEX CONCURRENTLY IF EXISTS idx_forensic_event_rawmessage_trgm;
CREATE INDEX CONCURRENTLY idx_forensic_event_rawmessage_trgm
  ON event USING gin ((data->>'rawMessage') gin_trgm_ops)
  WITH (fastupdate = off)
  WHERE event_type = 'MessageReceived';
