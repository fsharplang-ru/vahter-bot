DROP INDEX IF EXISTS user_username_uindex;

CREATE INDEX user_username_index ON "user" (username);
