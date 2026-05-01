-- Partial indexes for callback and ban queries on the event table.

-- getActiveCallbacksForUser: filters CallbackCreated by targetUserId
CREATE INDEX IF NOT EXISTS idx_event_callback_target_user
    ON event(((data->>'targetUserId')::BIGINT))
    WHERE event_type = 'CallbackCreated';

-- getOldCallbacksInChannel: filters CallbackCreated by actionChannelId + created_at
CREATE INDEX IF NOT EXISTS idx_event_callback_channel
    ON event(((data->>'actionChannelId')::BIGINT), created_at)
    WHERE event_type = 'CallbackCreated';

-- expireCallbacksByMessageId: filters CallbackMessagePosted by actionMessageId
CREATE INDEX IF NOT EXISTS idx_event_callback_posted_msg
    ON event(((data->>'actionMessageId')::INT))
    WHERE event_type = 'CallbackMessagePosted';

-- isBannedByVahter: filters UserBanned by vahterId inside bannedBy
CREATE INDEX IF NOT EXISTS idx_event_user_banned_vahter
    ON event(((data->'bannedBy'->>'vahterId')::BIGINT))
    WHERE event_type = 'UserBanned';
