Database setup

```postgresql
CREATE ROLE vahter_bot_ban_service WITH LOGIN PASSWORD 'vahter_bot_ban_service';
GRANT vahter_bot_ban_service TO postgres;
CREATE DATABASE vahter_bot_ban OWNER vahter_bot_ban_service ENCODING 'UTF8';
GRANT ALL ON DATABASE vahter_bot_ban TO vahter_bot_ban_service;
GRANT USAGE, CREATE ON SCHEMA public TO vahter_bot_ban_service;
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
```

Run migrations

```
flyway -configFiles=flyway.local.conf migrate
```
