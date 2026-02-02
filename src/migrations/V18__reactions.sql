-- Add reaction_count column to user table for tracking reaction spam
ALTER TABLE "user" ADD COLUMN reaction_count INT NOT NULL DEFAULT 0;
