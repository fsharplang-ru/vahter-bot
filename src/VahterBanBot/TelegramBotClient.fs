namespace VahterBanBot

open System
open System.Threading.Tasks
open Funogram.Telegram
open Funogram.Api
open Funogram.Telegram.Types
open Funogram.Types
 
type TelegramBotClient(config: BotConfig) =
    
    let sendAndUnwrap request = task {
        let! response = api config request |> Async.StartAsTask
        return
            match response with
            | Error e -> raise <| e.AsException()
            | Ok result -> result
    }
    
    let unixTimestamp(dto: DateTimeOffset) = dto.ToUnixTimeSeconds() 
    
    member _.SendTextMessageAsync(chatId: ChatId, text: string, ?parseMode: ParseMode, ?replyMarkup: Markup): Task<_> =
        Req.SendMessage.Make(chatId = chatId, text = text, ?parseMode = parseMode, ?replyMarkup = replyMarkup)
        |> sendAndUnwrap

    member _.GetChatAdministratorsAsync(chatId: ChatId): Task<_> =
        Req.GetChatAdministrators.Make chatId
        |> sendAndUnwrap
        
    member _.BanChatMemberAsync(chatId: ChatId, userId: int64, untilDate: DateTimeOffset) =
        Req.BanChatMember.Make(chatId, userId, untilDate = unixTimestamp untilDate)
        |> sendAndUnwrap

    member _.RestrictChatMemberAsync(chatId: ChatId, userId: int64, permissions: ChatPermissions, untilDate: DateTimeOffset) =
        Req.RestrictChatMember.Make(chatId, userId, permissions, untilDate = unixTimestamp untilDate)
        |> sendAndUnwrap
        
    member _.UnbanChatMemberAsync(chatId: ChatId, userId: int64) =
        Req.UnbanChatMember.Make(chatId, userId, onlyIfBanned = true)
        |> sendAndUnwrap
        
    member _.DeleteMessageAsync(chatId: ChatId, messageId: int64) =
        Req.DeleteMessage.Make(chatId, messageId)
        |> sendAndUnwrap
        
    member _.AnswerCallbackQueryAsync(callbackQueryId: string) =
        Req.AnswerCallbackQuery.Make callbackQueryId
        |> sendAndUnwrap