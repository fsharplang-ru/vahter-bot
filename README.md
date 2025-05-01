vahter-ban-bot
==============
This is a Telegram bot that deletes spam from chats and performs administrative functions.

Deployment
==========
Database setup
- run init.sql

Run migrations

```
flyway -configFiles=flyway.local.conf migrate
```

Administrator Guide
===================
**Global** administrators (set up via the bot config) can perform the following commands in any of the associated chats:
- `/ban ping` — will reply `pong` (to check whether bot is active in a chat).
- `/ban` _(posted as a reply to a message)_ — deletes the message and globally bans the user who posted it.
- `/sban [duration]` _(posted as a reply to a message)_ — delete the message and soft-ban (mute) the user in the current chat for a `duration` hours (default is 24 hours).
- `/unban <user_id>` — will unban the user with specified Telegram user id.

Additionally, any administrators (**global** and **local**) enjoy immunity from message auto-filtering.

Adding a Chat
-------------
To add a chat, a Telegram chat id (negative number) and its username is required.

Monitoring of chats without public user names is not supported, yet.
