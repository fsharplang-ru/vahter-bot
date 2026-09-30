CREATE TABLE moderation_quality_daily (
    day date NOT NULL,
    cohort text NOT NULL CHECK (cohort IN ('all_scored', 'llm_triaged')),
    system text NOT NULL CHECK (system IN ('pipeline', 'ml', 'llm')),
    llm_model text,
    tp bigint NOT NULL DEFAULT 0,
    tn bigint NOT NULL DEFAULT 0,
    fp bigint NOT NULL DEFAULT 0,
    fn bigint NOT NULL DEFAULT 0,
    abstained bigint NOT NULL DEFAULT 0,
    unresolved bigint NOT NULL DEFAULT 0,
    excluded bigint NOT NULL DEFAULT 0,
    computed_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE NULLS NOT DISTINCT (day, cohort, system, llm_model)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON moderation_quality_daily TO vahter_bot_ban_service;
INSERT INTO scheduled_job (job_name) VALUES ('moderation_quality_daily');
INSERT INTO bot_setting (key, value, type, feature_group, description) VALUES
    ('QUALITY_BACKFILL_DAYS', '7', 'FREE_FORM', 'quality', 'Maximum older missing days processed per explicit backfill invocation');

CREATE TABLE moderation_quality_pending (
    event_id bigint PRIMARY KEY,
    stream_id text NOT NULL
);
CREATE TABLE moderation_quality_dirty_day (day date PRIMARY KEY);
GRANT SELECT, INSERT, UPDATE, DELETE ON moderation_quality_pending, moderation_quality_dirty_day
    TO vahter_bot_ban_service;

CREATE FUNCTION moderation_quality_enqueue() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO moderation_quality_pending (event_id, stream_id)
    VALUES (NEW.id, CASE WHEN NEW.event_type = 'UserUnbanned' THEN NEW.stream_id
                        ELSE 'detection:' || substring(NEW.stream_id FROM position(':' IN NEW.stream_id) + 1) END);
    RETURN NULL;
END $$;
CREATE TRIGGER moderation_quality_enqueue AFTER INSERT ON event
    FOR EACH ROW WHEN (NEW.event_type IN (
        'MessageReceived', 'MessageEdited', 'MessageMarkedHam', 'MessageMarkedSpam',
        'MlScoredMessage', 'LlmClassified', 'LlmVerdictCacheHit',
        'VahterActed', 'BotAutoDeleted', 'UserUnbanned'))
    EXECUTE FUNCTION moderation_quality_enqueue();
