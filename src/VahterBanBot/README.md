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

`typesafe-jev-1.14.0` selects Typesafe Jev with the pinned `jev-1.14.0` model. Set `LLM_TRIAGE_LAYERS` to `["typesafe-jev-1.14.0","azure-sol-5.6"]` to enable Jev before Sol, or `["azure-sol-5.6"]` for Sol-only. `TYPESAFE_APIKEY` supplies the bearer token from the environment; a missing or blank key logs an error and skips Jev without stopping the bot.

Jev uses the shared moderation policy and frozen context with first-pass instructions and both `verdict` and `review_focus` Choice questions. Only a normalized, valid SPAM or NOT_SPAM answer meeting all three gates is accepted; review focus never influences Sol. Telegram logs and cached verdicts identify the deciding model. Jev supplies the selected-class probability as its reason (for example, `SPAM probability: 0.900`).

| Setting | Default | Meaning |
|---|---|---|
| `JEV_ENDPOINT` | `https://api.typesafe.ai/v1/systemone` | System One HTTP endpoint |
| `JEV_TIMEOUT_MS` | `3000` | Per-request deadline, 1–30000 milliseconds |
| `JEV_MIN_CONFIDENCE` | `0.50` | Minimum Choice confidence |
| `JEV_MIN_PROBABILITY` | `0.75` | Minimum chosen-class probability |
| `JEV_MIN_MARGIN` | `0.30` | Minimum lead over the next-highest class |

These settings are DB-backed and hot-reloadable. Thresholds must be finite numbers from 0 to 1. Jev makes one attempt per message and honors HTTP 429 `Retry-After` with a provider cooldown; errors, timeouts and uncertain results advance to the next layer. Cache identity includes the prompt, endpoint, model, context format, layer order and gate settings.

Jev exports `vahter_jev_tokens_total` with labels `model="jev-1.14.0"` and `direction="input"` or `direction="output"` through the existing OpenTelemetry meter. Counts come from the response's [request-wide usage](https://docs.typesafe.ai/api/system-one), including both questions and fallthrough to Sol after low confidence, failed validation or a model mismatch; cache hits, disabled layers, missing keys and cooldown skips add nothing.

`vahter_jev_usage_unavailable_total` uses the same labels and counts request attempts missing a valid nonnegative token count for that direction, including timeouts, HTTP errors and responses that cannot be deserialized into the typed response. Available directions still count independently; a reported zero is valid. Token totals reflect reported usage, not an exact billing ledger, and cannot include usage hidden by failed requests or lost on process termination before export. The model label is the requested pinned model, even if the returned model is rejected.

Each layer receives the same frozen evidence. SPAM or NOT_SPAM ends classification; SKIP, invalid output or service failure advances to the next layer. Exhaustion routes to human review. Azure content-filter handling follows `LLM_CONTENT_FILTER_IS_SPAM`. Unknown IDs and empty or duplicate orders fail settings loading before startup; rejected reloads retain the last valid configuration.

Providers register as `ILlmTriageLayer` services and expose a typed `Id`; the factory receives their collection through DI and selects them in settings order. Duplicate registrations are rejected. `LlmLayerOrder` guarantees non-empty, unique, supported IDs. `Configure` binds a settings snapshot and supplies model/prompt metadata, a cache identity covering request and acceptance settings, and a classification function. Layers perform classification only; the pipeline owns final event recording and caching, and the bot owns moderation actions.

Typesafe controls Jev releases and hosting. The request pins `jev-1.14.0`, following Typesafe's [version-pinning guidance](https://docs.typesafe.ai/models#aliases); moving aliases such as `jev-latest` are not used. A request failure or a different returned model ID logs an error and advances to the next layer. New releases require an explicit model-pin change and threshold evaluation. Version pinning does not guarantee indefinite upstream availability or let the bot detect changes served under the same model ID.

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
