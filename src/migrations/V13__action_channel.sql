-- Extend callback table for action channel support
ALTER TABLE callback 
ADD COLUMN action_message_id INTEGER NULL,
ADD COLUMN action_topic_id INTEGER NULL,
ADD COLUMN target_user_id BIGINT NULL;

CREATE INDEX idx_callback_target_user ON callback(target_user_id);
CREATE INDEX idx_callback_topic ON callback(action_topic_id);

-- New table for vahter action tracking (with race condition protection)
CREATE TABLE vahter_actions (
    id BIGSERIAL PRIMARY KEY,
    vahter_id BIGINT NOT NULL REFERENCES "user"(id),
    action_type TEXT NOT NULL,
    target_user_id BIGINT NOT NULL,
    target_message_id INTEGER NOT NULL,
    target_chat_id BIGINT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT timezone('utc'::TEXT, NOW()),
    
    -- One action per message (race condition protection)
    UNIQUE (target_chat_id, target_message_id)
);

CREATE INDEX idx_vahter_actions_vahter ON vahter_actions(vahter_id);
CREATE INDEX idx_vahter_actions_created ON vahter_actions(created_at);
