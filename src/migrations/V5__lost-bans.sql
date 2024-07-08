ALTER TABLE banned
    ALTER COLUMN banned_at TYPE TIMESTAMPTZ USING banned_at::TIMESTAMPTZ,
    ALTER COLUMN message_text DROP NOT NULL;

INSERT INTO banned (message_text, banned_user_id, banned_at, banned_by, message_id, banned_in_chat_id, banned_in_chat_username)
SELECT b.ban_reason 
     , b.id
     , b.banned_at
     , vahter.id
     , NULL -- don't have it
     , NULL -- don't have it
     , NULL -- don't have it
FROM "user" b
         JOIN "user" vahter ON vahter.id = b.banned_by
WHERE b.ban_reason IS NULL -- missed such messages in the previous migration
  AND b.banned_at IS NOT NULL;
