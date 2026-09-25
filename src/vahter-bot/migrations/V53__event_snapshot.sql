-- Aggregate snapshots for BotInfra.EventStore (see src/BotInfra/EVENTSTORE.md). A disposable
-- cache: truncating it is always safe, loads fall back to a full stream replay.
CREATE TABLE IF NOT EXISTS event_snapshot (
    stream_id      TEXT        NOT NULL,
    state_type     TEXT        NOT NULL,
    schema_version INT         NOT NULL,
    stream_version INT         NOT NULL,
    state          JSONB       NOT NULL,
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (stream_id, state_type)
);

-- Snapshots are only valid for an append-only log: any in-place rewrite of events drops them.
CREATE OR REPLACE FUNCTION event_snapshot_invalidate() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'TRUNCATE' THEN
        TRUNCATE event_snapshot;
    ELSE
        DELETE FROM event_snapshot WHERE stream_id = OLD.stream_id OR stream_id = NEW.stream_id;
    END IF;
    RETURN NULL;
END $$;
CREATE OR REPLACE TRIGGER event_snapshot_invalidate AFTER UPDATE OR DELETE ON event
    FOR EACH ROW EXECUTE FUNCTION event_snapshot_invalidate();
CREATE OR REPLACE TRIGGER event_snapshot_invalidate_truncate AFTER TRUNCATE ON event
    FOR EACH STATEMENT EXECUTE FUNCTION event_snapshot_invalidate();

GRANT SELECT, INSERT, UPDATE, DELETE ON event_snapshot TO vahter_bot_ban_service;
