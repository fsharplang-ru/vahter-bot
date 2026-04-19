-- V27: Backfill MessageReceived + BotAutoDeleted events for legacy spam messages
-- that were deleted from the message table before V23 ran.
--
-- Root cause: a scheduled cleanup job deleted old rows from the message table.
-- Spam messages (caught by bot or vahters) were purged before V23 migrated.
-- V23 only created MessageReceived events from message table rows still present,
-- so those historical spam messages have no MessageReceived event.
-- The training query (DB.fs mlData) starts from MessageReceived and can't see them.
--
-- All INSERTs use ON CONFLICT DO NOTHING for idempotency (safe to re-run).

-- Step 1: MessageReceived from banned_by_bot
-- Backfills messages the bot auto-deleted that were not in message table at V23 time.
INSERT INTO event(stream_id, stream_version, data, created_at)
SELECT 'message:' || bbb.banned_in_chat_id || ':' || bbb.message_id,
       1,
       jsonb_build_object(
           'Case', 'MessageReceived',
           'chatId', bbb.banned_in_chat_id,
           'messageId', bbb.message_id,
           'userId', bbb.banned_user_id,
           'text', bbb.message_text,
           'rawMessage', '{}'::jsonb
       ),
       bbb.banned_at
FROM banned_by_bot bbb
WHERE bbb.message_id IS NOT NULL
  AND bbb.banned_in_chat_id IS NOT NULL
  AND bbb.message_text IS NOT NULL
ON CONFLICT (stream_id, stream_version) DO NOTHING;

-- Step 2: BotAutoDeleted from banned_by_bot (spam label for the training query)
-- The training query looks for event_type IN ('BotAutoDeleted','VahterActed')
-- with actionType IS NULL — BotAutoDeleted matches that condition.
INSERT INTO event(stream_id, stream_version, data, created_at)
SELECT 'moderation:' || bbb.banned_in_chat_id || ':' || bbb.message_id,
       1,
       jsonb_build_object(
           'Case', 'BotAutoDeleted',
           'chatId', bbb.banned_in_chat_id,
           'messageId', bbb.message_id,
           'userId', bbb.banned_user_id,
           'reason', jsonb_build_object('Case', 'MlSpam', 'score', 1.0)
       ),
       bbb.banned_at
FROM banned_by_bot bbb
WHERE bbb.message_id IS NOT NULL
  AND bbb.banned_in_chat_id IS NOT NULL
ON CONFLICT (stream_id, stream_version) DO NOTHING;

-- Step 3: MessageReceived from banned (vahter manual bans)
-- V23 already created VahterActed events in moderation:chatId:messageId from vahter_actions.
-- Only MessageReceived is missing for messages purged before V23.
INSERT INTO event(stream_id, stream_version, data, created_at)
SELECT 'message:' || b.banned_in_chat_id || ':' || b.message_id,
       1,
       jsonb_build_object(
           'Case', 'MessageReceived',
           'chatId', b.banned_in_chat_id,
           'messageId', b.message_id,
           'userId', b.banned_user_id,
           'text', b.message_text,
           'rawMessage', '{}'::jsonb
       ),
       b.banned_at
FROM banned b
WHERE b.message_id IS NOT NULL
  AND b.banned_in_chat_id IS NOT NULL
  AND b.message_text IS NOT NULL
ON CONFLICT (stream_id, stream_version) DO NOTHING;
