-- Cache of Azure Computer Vision OCR responses keyed by Telegram's stable
-- per-content file_unique_id. On cache hit the bot skips file download and
-- the Azure call entirely.
CREATE TABLE ocr_cache (
    file_unique_id TEXT        PRIMARY KEY,
    azure_response JSONB       NOT NULL,
    extracted_text TEXT,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

GRANT SELECT, INSERT ON ocr_cache TO vahter_bot_ban_service;
