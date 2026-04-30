-- V24: Fix rawMessage field in migrated MessageReceived events.
-- V23 used raw_message::jsonb which stored rawMessage as a JSONB object.
-- The F# type expects rawMessage: string (a serialized JSON string).
-- Only rows where rawMessage is still an object are updated; already-correct rows are skipped.

UPDATE event
SET data = jsonb_set(data, '{rawMessage}', to_jsonb(data->>'rawMessage'))
WHERE event_type = 'MessageReceived'
  AND jsonb_typeof(data->'rawMessage') = 'object';
