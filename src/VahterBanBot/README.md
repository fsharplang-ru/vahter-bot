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

### Join Messages

`DELETE_JOIN_MESSAGES_IN_CHATS` in `bot_setting` is a JSON array of monitored chat IDs, for example `[-1001234567890]`; its default `[]` disables deletion everywhere. Listed chats have all member join service messages deleted, including messages announcing multiple members. The bot needs permission to delete messages.

After changing the setting, call the authenticated `POST /reload-settings` endpoint. Leave messages and ordinary messages retain their usual handling.

## Spam Detection

LLM triage runs the ordered layer IDs in `bot_setting.LLM_TRIAGE_LAYERS`, a non-empty JSON array defaulting to `["azure-sol-5.6"]`. `azure-sol-5.6` selects the Sol 5.6 provider and prompt, using the configured Azure endpoint, deployment alias and reasoning effort. `LLM_TRIAGE_ENABLED` controls eligibility; changes take effect through authenticated `POST /reload-settings`.

Each layer receives the same frozen evidence. SPAM or NOT_SPAM ends classification; SKIP, invalid output or service failure advances to the next layer. Exhaustion routes to human review. Azure content-filter handling follows `LLM_CONTENT_FILTER_IS_SPAM`. Unknown IDs and empty or duplicate orders fail settings loading before startup; rejected reloads retain the last valid configuration.

Providers implement `ILlmTriageLayer`; versioned `LlmLayerId` cases resolve through an exhaustive factory. `LlmLayerOrder` preserves configured order and guarantees non-empty, unique, supported IDs. `Configure` binds a settings snapshot and supplies model/prompt metadata, a cache identity covering request and acceptance settings, and a classification function. Layers perform classification only; the pipeline owns final event recording and caching, and the bot owns moderation actions.

SPAM/SKIP cache entries retain the deciding provider's metadata and match the ordered pipeline's configuration fingerprint. Layer order, model, prompt, reasoning effort or chat-policy changes require fresh classification. Cache scope, TTLs and human-correction invalidation use the existing text keys.

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
