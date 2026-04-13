# VahterBanBot

Telegram bot that deletes spam from chats and performs administrative functions for the Russian-speaking F# community.

## Administrator Guide

**Global** administrators (set up via bot config) can use these commands in any associated chat:

- `/ban ping` — replies `pong` (health check)
- `/ban` _(reply to a message)_ — deletes the message and globally bans the user
- `/sban [duration]` _(reply to a message)_ — deletes the message and soft-bans (mutes) the user in the current chat for `duration` hours (default: 24)
- `/unban <user_id>` — unbans the user with the specified Telegram user ID

Administrators (both **global** and **local**) are immune from message auto-filtering.

### Adding a Chat

To add a chat, a Telegram chat ID (negative number) and its username are required.

## Spam Detection

Uses LLM-based spam detection (OpenAI API) with configurable verdicts: SPAM, NOT_SPAM, SKIP.

## Deployment

Container images pin architecture-specific .NET base images (`arm64v8`) for reproducible builds on Ampere VMs.

- **GHCR image**: `ghcr.io/szer/vahter-bot`
- **Deploy**: push to `main` triggers CI/CD via GitHub Actions → GHCR → ArgoCD

## Database

- **Database**: `vahter_db_v2`
- **Migrations**: `src/vahter-bot/migrations/` (Flyway)
- Run locally: `flyway -configFiles=flyway.local.conf migrate`

## License

MIT. See [LICENSE](LICENSE).
