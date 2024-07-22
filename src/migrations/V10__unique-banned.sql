DROP INDEX IF EXISTS banned_banned_user_id_idx;
CREATE UNIQUE INDEX banned_banned_user_id_idx ON banned (banned_user_id);
