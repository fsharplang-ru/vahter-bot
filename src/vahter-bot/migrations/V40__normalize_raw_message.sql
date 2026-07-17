-- Normalize event rawMessage to the single canonical shape: a JSON *string*
-- (what the live app has always written). Closes the data half of issue #166.
--
-- History: V23's backfill wrote rawMessage as JSONB objects; V25 stringified
-- those. V27's spam backfill then (re)introduced ~246k rows with
-- 'rawMessage': '{}'::jsonb — an empty OBJECT — which are the only
-- object-form rows left in prod. This turns them into the string '"{}"' so
-- readers and the F# MessageEvent DU (rawMessage: string) can assume one shape.
-- Idempotent: already-string rows don't match the WHERE.

UPDATE event
SET data = jsonb_set(data, '{rawMessage}', to_jsonb(data->>'rawMessage'))
WHERE event_type IN ('MessageReceived', 'MessageEdited')
  AND jsonb_typeof(data->'rawMessage') = 'object';
