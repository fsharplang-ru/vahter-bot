-- Partial index on MlScoredMessage's score so the LLM-band range scan (WHERE score BETWEEN -0.5 AND 1.5)
-- hits an index instead of scanning 108k rows to find 3.7k (24s/query on prod); only MlScoredMessage rows pay for it.
DROP INDEX CONCURRENTLY IF EXISTS idx_event_mlscored_score_created_at;
CREATE INDEX CONCURRENTLY idx_event_mlscored_score_created_at
    ON event (((data->>'score')::double precision), created_at)
    WHERE event_type = 'MlScoredMessage';
