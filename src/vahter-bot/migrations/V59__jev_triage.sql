INSERT INTO bot_setting (key, value, type, feature_group, description)
VALUES
    ('JEV_ENDPOINT', 'https://api.typesafe.ai/v1/systemone', 'FREE_FORM', 'LLM', 'Typesafe System One endpoint.'),
    ('JEV_TIMEOUT_MS', '3000', 'FREE_FORM', 'LLM', 'Jev request deadline in milliseconds (1-30000); failure advances to the next layer.'),
    ('JEV_MIN_CONFIDENCE', '0.50', 'FREE_FORM', 'LLM', 'Minimum Jev Choice confidence for an automatic verdict.'),
    ('JEV_MIN_PROBABILITY', '0.75', 'FREE_FORM', 'LLM', 'Minimum chosen-class probability for an automatic Jev verdict.'),
    ('JEV_MIN_MARGIN', '0.30', 'FREE_FORM', 'LLM', 'Minimum Jev chosen-class probability minus the largest other-class probability.')
ON CONFLICT (key) DO NOTHING;

UPDATE bot_setting
SET description = 'Ordered unique LLM layer IDs; SKIP or failure advances to the next layer. Available: typesafe-jev-1.14.0, azure-sol-5.6.'
WHERE key = 'LLM_TRIAGE_LAYERS';
