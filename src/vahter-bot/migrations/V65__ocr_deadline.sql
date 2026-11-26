INSERT INTO bot_setting (key, value, type, feature_group, description)
VALUES ('OCR_DEADLINE_SECONDS', '5', 'FREE_FORM', 'OCR', 'Maximum seconds to wait for Azure OCR (1 to 5).')
ON CONFLICT (key) DO NOTHING;
