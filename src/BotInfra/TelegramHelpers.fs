namespace BotInfra

open System.Text.Json.Serialization
open Telegram.Bot

/// Shared Telegram helpers.
module TelegramHelpers =
    // needed for STJ
    let telegramJsonOptions =
        let baseOpts = Microsoft.AspNetCore.Http.Json.JsonOptions()
        JsonBotAPI.Configure(baseOpts.SerializerOptions)

        // HACK TIME
        // there is a contradiction in Telegram.Bot library where User.IsBot is not nullable and required during deserialization,
        // but it is omitted when default on deserialization via settings setup in JsonBotAPI.Configure
        // so we'll override this setting explicitly
        baseOpts.SerializerOptions.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull

        baseOpts.SerializerOptions

module TelegramExtensions =
    type Telegram.Bot.Types.Update with
        member msg.EditedOrMessage =
            if isNull msg.EditedMessage then
                msg.Message
            else
                msg.EditedMessage
