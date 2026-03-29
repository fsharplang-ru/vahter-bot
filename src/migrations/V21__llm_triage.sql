-- LLM-based triage shadow classification
-- Stores verdicts from LLM for later accuracy analysis against actual vahter decisions
CREATE TABLE llm_triage (
    id                BIGSERIAL    PRIMARY KEY,
    chat_id           BIGINT       NOT NULL,
    message_id        INT          NOT NULL,
    user_id           BIGINT       NOT NULL,
    verdict           TEXT         NOT NULL,  -- 'KILL' | 'SPAM' | 'NOT_SPAM'
    prompt_tokens     INT,
    completion_tokens INT,
    latency_ms        INT,
    created_at        TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);

CREATE INDEX ix_llm_triage_chat_message ON llm_triage (chat_id, message_id);
CREATE INDEX ix_llm_triage_created_at   ON llm_triage (created_at);

-- Service account used by the pod (same pattern as all previous migrations)
GRANT SELECT, INSERT ON llm_triage                 TO vahter_bot_ban_service;
GRANT USAGE, SELECT  ON SEQUENCE llm_triage_id_seq TO vahter_bot_ban_service;
