-- Fix botched V23 migration: VahterActed.actionType was stored as a plain string
-- (e.g. "PotentialKill") but the F# serializer (WithUnionInternalTag) produces
-- an embedded-tag object (e.g. {"Case":"PotentialKill"}).
-- Normalise all plain-string values to the object form so deserialization works.
UPDATE event
SET data = jsonb_set(data, '{actionType}', jsonb_build_object('Case', data->>'actionType'))
WHERE event_type = 'VahterActed'
  AND jsonb_typeof(data->'actionType') = 'string';
