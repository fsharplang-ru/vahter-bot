-- Table for distributed scheduled job execution with lease mechanism
-- Ensures jobs run exactly once per day at scheduled time across multiple pods
-- Scheduled hours are configured via environment variables, not stored in DB

CREATE TABLE scheduled_job (
    job_name TEXT PRIMARY KEY,
    last_completed_at TIMESTAMPTZ,      -- when job actually completed successfully
    locked_until TIMESTAMPTZ,           -- lease expiration time
    locked_by TEXT,                     -- pod identifier for debugging
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Seed the jobs (scheduled hours come from config)
INSERT INTO scheduled_job (job_name) VALUES ('daily_cleanup');
INSERT INTO scheduled_job (job_name) VALUES ('daily_stats');

-- Grant permissions
GRANT SELECT, INSERT, UPDATE, DELETE ON scheduled_job TO vahter_bot_ban_service;
