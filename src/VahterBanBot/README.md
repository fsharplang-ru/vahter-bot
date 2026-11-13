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

Vahter tries to queue each eligible notice in PostgreSQL with a one-second timeout. If the enqueue fails, Vahter logs an error with the chat and message IDs and acknowledges the webhook so Telegram can deliver the next update.

One poller per pod claims work only when one of four deletion slots is waiting on its zero-capacity channel. It starts by looking for up to 16 ready jobs in one chat. A full result doubles the next limit up to `JOIN_DELETE_MAX_CLAIM` (default 100) and triggers an immediate poll when a slot is free. At least half full waits 500 ms. Below half waits one second and halves the limit toward 16, bounded by the configured cap.

Above `JOIN_DELETE_BULK_THRESHOLD` (default 2), a slot sends one `deleteMessages` request with the available IDs. At or below the threshold it takes one ID for `deleteMessage`, and immediately hands further known-ready jobs to free slots. Both settings accept 1–100 and reload with `POST /reload-settings`. Telegram accepts up to 100 IDs per call.

Bulk success records `JoinNotificationBulkDeleteAccepted` per ID because Telegram skips absent messages. Single-message success records `JoinNotificationDeleted`. A rejected bulk request with HTTP 400 returns its IDs to individual processing. Flood waits are shared across pods and abandoned claims expire after three minutes.

After changing the setting, call the authenticated `POST /reload-settings` endpoint. Leave messages and ordinary messages retain their usual handling.

### Delayed join-history checks

With `TELEGRAM_API_ID` and `TELEGRAM_API_HASH` in the pod environment, Vahter uses its bot token for an in-memory MTProto session. Enrolled supergroups with a resolvable Telegram username trigger a history check when at least 32 distinct join notices were queued for one Telegram UTC minute and arrivals for that bucket have been quiet for two minutes.

One replica reads IDs in pages of 100 between the first and last observed notice, with 100 IDs of padding on each side. It deletes only join service actions in that supergroup and minute, then requires an explicit empty history result for every deleted ID. Gaps without a qualifying observed minute or outside the padded range are outside the scan.

PostgreSQL checkpoints each verified page. A failed page retries after a minute up to three failures, logged at Error. MTProto flood waits pause both replicas and preserve the retry budget. Checks expire after 47 hours.

#### Measuring a history sweep

In Tempo, search for `{ name = "joinHistory.page" }`. Each page attempt carries the chat, UTC bucket minute, cursor, requested ID count, queued notice count and checkpoint outcome. Child spans time the initial read, delete and verification; cold reads include nested login and username resolution. Errors carry the exception type and numeric Telegram RPC code when available.

| Question | OpenTelemetry instrument |
|---|---|
| How long after the bucket is due does the first page start? | `vahter_join_history_start_delay` |
| How long from the end of the Telegram minute until the bucket is fully checked? | `vahter_join_history_completion_age` |
| Which step takes time or fails? | `vahter_join_history_operation_duration` by `operation` and `outcome` |
| How many scanned IDs yield a surviving join and a verified deletion? | `vahter_join_history_ids_total` by `stage` (`scanned`, `found`, `delete_accepted`, `verified`) |
| How old are the notices when removal is verified? | `vahter_join_history_verified_age` |
| Are pages advancing, retrying or exhausting attempts? | `vahter_join_history_pages_total` by `outcome`, and `vahter_join_history_flood_wait` for the requested cooldown |

Histograms use seconds. ID counters count observations across attempts, so retries can count an ID again. In Loki, group start, completion and error records by `ChatId` and `BucketMinute`. Verified deletion records include `MessageIds` to correlate with Bot API receipts. A completed bucket can reopen on a late webhook. These measurements cover the delayed sweep; earlier MTProto visibility requires a live observation.

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

The startup setting `DB_MAX_POOL_SIZE` defaults to three connections per Vahter process. `DATABASE_URL` supplies an additional upper bound. Two replicas can use six connections, and four overlapping processes can use twelve. The settings listener, workers and webhook requests share the pool. Changes apply on the next process start.

- **Database**: `vahter_db_v2`
- **Migrations**: `src/vahter-bot/migrations/` (Flyway)
- Run locally: `flyway -configFiles=flyway.local.conf migrate`

## License

MIT. See [LICENSE](LICENSE).
