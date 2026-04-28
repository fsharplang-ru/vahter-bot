-- ML model storage (singleton row)
-- Stores the serialized ML.NET model for fast startup and cross-pod sharing
CREATE TABLE ml_trained_model (
    id INT PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    model_data BYTEA NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

GRANT SELECT, INSERT, UPDATE ON ml_trained_model TO vahter_bot_ban_service;

-- New scheduled job for daily ML retraining
INSERT INTO scheduled_job (job_name) VALUES ('daily_ml_retrain');
