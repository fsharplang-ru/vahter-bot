module VahterBanBot.Metrics

open System
open System.Collections.Generic
open System.Diagnostics.Metrics
open VahterBanBot.Types

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


