-- Migration from forum topics to separate channels
-- Delete old callbacks (they reference the old forum channel with topics)
DELETE FROM callback;

-- Rename action_topic_id to action_channel_id and change type to BIGINT
ALTER TABLE callback 
DROP COLUMN action_topic_id;

ALTER TABLE callback 
ADD COLUMN action_channel_id BIGINT NULL;

CREATE INDEX idx_callback_channel ON callback(action_channel_id);
DROP INDEX IF EXISTS idx_callback_topic;
