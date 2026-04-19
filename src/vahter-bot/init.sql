CREATE ROLE admin WITH LOGIN PASSWORD 'admin'; -- no need for prod DB
CREATE ROLE vahter_bot_ban_service WITH LOGIN PASSWORD 'vahter_bot_ban_service'; -- change password for prod
GRANT vahter_bot_ban_service TO postgres; -- no need for prod DB
CREATE DATABASE vahter_db OWNER admin ENCODING 'UTF8';
GRANT CONNECT ON DATABASE vahter_db TO vahter_bot_ban_service;
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
