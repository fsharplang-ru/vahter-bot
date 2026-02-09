vahter-ban-bot
==============
This is a Telegram bot that deletes spam from chats and performs administrative functions.

Deployment
----------
### Container images
The Dockerfile pins architecture-specific .NET base images (`mcr.microsoft.com/dotnet/sdk:10.0-noble-arm64v8` and `mcr.microsoft.com/dotnet/aspnet:10.0-noble-arm64v8`) to avoid pulling mismatched manifests on Arm64 hosts such as Ampere VMs.

* **Pros:** reproducible builds that cannot accidentally resolve to a different architecture when Docker defaults change; clearer compatibility expectations for operators.
* **Cons:** the image cannot be reused on non-Arm64 builders/runtimes without editing the tags; AMD64 targets would require switching tags to variants such as `10.0-noble-amd64`.

GitHub Actions runs the CI/CD workflows on AMD64 `ubuntu-latest` runners. The deploy workflow cross-builds and pushes an Arm64 image with QEMU and Buildx (`platforms: linux/arm64`). If you later need AMD64 images, switch the platform and tags accordingly or run the workflow on Arm64 runners.

Database setup
- run init.sql

Run migrations

```
flyway -configFiles=flyway.local.conf migrate
```

Administrator Guide
-------------------
**Global** administrators (set up via the bot config) can perform the following commands in any of the associated chats:
- `/ban ping` — will reply `pong` (to check whether bot is active in a chat).
- `/ban` _(posted as a reply to a message)_ — deletes the message and globally bans the user who posted it.
- `/sban [duration]` _(posted as a reply to a message)_ — delete the message and soft-ban (mute) the user in the current chat for a `duration` hours (default is 24 hours).
- `/unban <user_id>` — will unban the user with specified Telegram user id.

Additionally, any administrators (**global** and **local**) enjoy immunity from message auto-filtering.

### Adding a Chat
To add a chat, a Telegram chat id (negative number) and its username is required.

Monitoring of chats without public user names is not supported, yet.
