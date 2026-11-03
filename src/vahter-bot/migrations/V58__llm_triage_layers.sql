ALTER TABLE llm_verdict_cache
    ADD COLUMN prompt_hash text,
    ADD COLUMN policy_hash text;

INSERT INTO bot_setting (key, value, type, feature_group, description)
VALUES ('LLM_TRIAGE_LAYERS', '["azure-sol-5.6"]', 'JSON_BLOB', 'LLM',
        'Ordered unique LLM layer IDs; SKIP or failure advances to the next layer. Available: azure-sol-5.6.')
ON CONFLICT (key) DO NOTHING;
