module VahterBanBot.Metrics

open System
open System.Collections.Generic
open System.Diagnostics.Metrics
open VahterBanBot.Types
open VahterBanBot.Utils

// Custom metrics
let meter = new Meter("VahterBanBot.Metrics", "1.0.0")

let messagesProcessedCounter =
    meter.CreateCounter<int64>(
        "vahter_messages_processed_total",
        "messages",
        "Total number of messages processed by the bot"
    )

let deletedMessagesCounter =
    meter.CreateCounter<int64>(
        "vahter_messages_deleted_total",
        "messages",
        "Total number of messages deleted by the bot"
    )

let bannedUsersCounter =
    meter.CreateCounter<int64>(
        "vahter_users_banned_total",
        "users",
        "Total number of users banned by vahters"
    )

let spamWarningsSentCounter =
    meter.CreateCounter<int64>(
        "vahter_spam_warnings_sent_total",
        "warnings",
        "Total number of ephemeral spam warnings sent to users after auto-deletion"
    )

let spamProtectionsGrantedCounter =
    meter.CreateCounter<int64>(
        "vahter_spam_protections_granted_total",
        "grants",
        "Total number of temporary spam-protection windows granted after a ham mark on an auto-deleted message"
    )

let spamProtectionDemotionsCounter =
    meter.CreateCounter<int64>(
        "vahter_spam_protection_demotions_total",
        "demotions",
        "Total number of would-be auto-deletions demoted to report-only for a protected user"
    )

let spamProtectionsRevokedCounter =
    meter.CreateCounter<int64>(
        "vahter_spam_protections_revoked_total",
        "revocations",
        "Total number of spam-protection windows revoked before their natural expiry, tagged by reason"
    )

let spamTextCacheSeedsCounter =
    meter.CreateCounter<int64>(
        "vahter_spam_text_cache_seeds_total",
        "seeds",
        "Total number of ban-seeded spam-text cache entries added from manual /ban"
    )

let spamTextCacheHitsCounter =
    meter.CreateCounter<int64>(
        "vahter_spam_text_cache_hits_total",
        "hits",
        "Total number of ban-seeded spam-text cache hits, tagged by mode"
    )

let tagsForSpamTextCacheMode (mode: SpamTextCacheMode) =
    [| KeyValuePair("mode", box (caseName mode)) |]

let llmVerdictCacheTotalCounter =
    meter.CreateCounter<int64>(
        "vahter_llm_verdict_cache_total",
        "lookups",
        "Total number of LLM verdict cache lookups, tagged by hit/miss and which tier (result=hit|miss, scope=global|sender|none)"
    )

let tagsForLlmVerdictCache (result: string) (scope: string) =
    [| KeyValuePair("result", box result); KeyValuePair("scope", box scope) |]

let tagsForChat (chatId: int64) (chatUsername: string) =
    [|
        KeyValuePair("chat_id", box chatId)
        KeyValuePair("chat_username", box (if isNull chatUsername then "" else chatUsername))
    |]

let tagsForDeletedMessage (chatId: int64) (chatUsername: string) (reason: string) =
    [|
        KeyValuePair("chat_id", box chatId)
        KeyValuePair("chat_username", box (if isNull chatUsername then "" else chatUsername))
        KeyValuePair("reason", box reason)
    |]

let tagsForVahter (actor: Actor): KeyValuePair<string, obj> array =
    [|
        match actor with
        | Actor.User user ->
            yield KeyValuePair("vahter_type", "user")
            yield KeyValuePair("vahter_id", box user.userId)
            yield KeyValuePair("vahter_username", box (defaultArg user.username ""))
        | Actor.Bot None ->
            yield KeyValuePair("vahter_type", "bot")
        | Actor.Bot (Some bot) ->
            yield KeyValuePair("vahter_type", "bot")
            yield KeyValuePair("vahter_id", box bot.botUserId)
            yield KeyValuePair("vahter_username", bot.botUsername)
        | Actor.ML ->
            yield KeyValuePair("vahter_type", "ml")
        | Actor.LLM llm ->
            yield KeyValuePair("vahter_type", "llm")
            yield KeyValuePair("vahter_model", llm.modelName)
            yield KeyValuePair("vahter_prompt_hash", llm.promptHash)
    |]

let recordDeletedMessage (chatId: int64) (chatUsername: string) (reason: string) =
    deletedMessagesCounter.Add(1L, tagsForDeletedMessage chatId chatUsername reason)

let recordDeletedMessagesBatch (chatId: int64) (chatUsername: string) (count: int) (reason: string) =
    if count > 0 then
        deletedMessagesCounter.Add(int64 count, tagsForDeletedMessage chatId chatUsername reason)

let recordSpamWarningSent (chatId: int64) (chatUsername: string) =
    spamWarningsSentCounter.Add(1L, tagsForChat chatId chatUsername)

let recordSpamProtectionGranted (chatId: int64) (chatUsername: string) =
    spamProtectionsGrantedCounter.Add(1L, tagsForChat chatId chatUsername)

let recordSpamProtectionDemotion (chatId: int64) (chatUsername: string) =
    spamProtectionDemotionsCounter.Add(1L, tagsForChat chatId chatUsername)

let tagsForSpamProtectionRevoke (chatId: int64) (chatUsername: string) (reason: string) =
    [|
        KeyValuePair("chat_id", box chatId)
        KeyValuePair("chat_username", box (if isNull chatUsername then "" else chatUsername))
        KeyValuePair("reason", box reason)
    |]

let recordSpamProtectionRevoked (chatId: int64) (chatUsername: string) (reason: string) =
    spamProtectionsRevokedCounter.Add(1L, tagsForSpamProtectionRevoke chatId chatUsername reason)


