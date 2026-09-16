namespace BotInfra

open Funogram.Telegram.Types

/// Telegram-message helpers shared across bots.
module TelegramMessage =

    /// True for a bot command: a `bot_command` entity at offset 0, or (entities absent,
    /// e.g. some test fixtures) text starting with "/". Never true for a caption.
    let isCommand (msg: Message) : bool =
        let hasCommandEntity =
            msg.Entities
            |> Option.exists (Array.exists (fun e -> e.Type = "bot_command" && e.Offset = 0L))
        hasCommandEntity || (msg.Text |> Option.exists (fun t -> t.StartsWith("/")))
