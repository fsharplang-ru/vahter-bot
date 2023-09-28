module VahterBanBot.Bot

open System
open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open VahterBanBot.Types
open VahterBanBot.Utils

let isChannelMessage (message: Message) =
    message.From.IsBot &&
    message.From.FirstName = "Channel" &&
    message.From.Username = "Channel_Bot"
    
let isPingCommand (message: Message) =
    message.Text = "/ban ping"

let isBanOnReplyMessage (message: Message) =
    message.Text = "/ban" &&
    message.ReplyToMessage <> null
    
let isMessageFromAllowedChats (botConfig: BotConfiguration) (message: Message) =
    botConfig.ChatsToMonitor.ContainsValue message.Chat.Id
    
let isMessageFromAdmin (botConfig: BotConfiguration) (message: Message) =
    botConfig.AllowedUsers.ContainsValue message.From.Id

let isBannedPersonAdmin (botConfig: BotConfiguration) (message: Message) =
    botConfig.AllowedUsers.ContainsValue message.ReplyToMessage.From.Id
    
let isBanAuthorized (botConfig: BotConfiguration) (message: Message) (logger: ILogger) =
    let fromUserId = message.From.Id
    let fromUsername = message.From.Username
    let targetUserId = message.ReplyToMessage.From.Id
    let targetUsername = message.ReplyToMessage.From.Username
    let chatId = message.Chat.Id
    let chatUsername = message.Chat.Username
    
    // check that user is allowed to ban others
    if isMessageFromAdmin botConfig message then
        if not(isMessageFromAllowedChats botConfig message) then
            logger.LogWarning $"User {fromUsername} {fromUserId} tried to ban user {targetUsername} ({targetUserId}) from not allowed chat {chatUsername} ({chatId})"
            false
        // check that user is not trying to ban other admins
        elif isBannedPersonAdmin botConfig message then
            logger.LogWarning $"User {fromUsername} ({fromUserId}) tried to ban admin {targetUsername} ({targetUserId}) in chat {chatUsername} ({chatId}"
            false
        else
            true
    else
        logger.LogWarning $"User {fromUsername} ({fromUserId}) tried to ban user {targetUsername} ({targetUserId}) without being admin in chat {chatUsername} ({chatId}"
        false
    
let banInAllChats (botConfig: BotConfiguration) (botClient: ITelegramBotClient) targetUserId = task {
    let banTasks =
        botConfig.ChatsToMonitor
        |> Seq.map (fun (KeyValue(chatUserName, chatId)) -> task {
            // ban user in each chat
            try
                do! botClient.BanChatMemberAsync(ChatId chatId, targetUserId, DateTime.UtcNow.AddMonths 13)
                return Ok(chatUserName, chatId) 
            with e ->
                return Error (chatUserName, chatId, e)
        })
    return! Task.WhenAll banTasks
}

let aggregateBanResultInLogMsg
    (logger: ILogger)
    (message: Message)
    (deletedUserMessages: int)
    (banResults: Result<string * int64, string * int64 * exn> []) =

    let vahterUserId = message.From.Id
    let vahterUsername = message.From.Username
    
    let targetUserId = message.ReplyToMessage.From.Id
    let targetUsername = message.ReplyToMessage.From.Username
    let logMsgBuilder = StringBuilder()
    %logMsgBuilder.AppendLine($"Vahter {prependUsername vahterUsername}({vahterUserId}) banned {prependUsername targetUsername} ({targetUserId})")
    %logMsgBuilder.AppendLine($"Deleted {deletedUserMessages} messages in chats:")

    (logMsgBuilder, banResults)
    ||> Array.fold (fun (sb: StringBuilder) result ->
        match result with
        | Ok (chatUsername, chatId) ->
            sb.AppendLine($"{prependUsername chatUsername} ({chatId}) - OK")
        | Error (chatUsername, chatId, e) ->
            logger.LogError($"Failed to ban user {prependUsername targetUsername} ({targetUserId}) in chat {prependUsername chatUsername} ({chatId})", e)
            sb.AppendLine($"{prependUsername chatUsername} ({chatId}) - FAILED. {e.Message}")
    )
    |> string

let onUpdate
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (message: Message) = task {

    // early return if if we can't process it
    if isNull message || isNull message.From then
        logger.LogWarning "Received update without message"
    else

    // upserting user to DB
    let! _ =
        DbUser.newUser message.From
        |> DB.upsertUser

    // check if message comes from channel, we should delete it immediately
    if botConfig.ShouldDeleteChannelMessages && isChannelMessage message then
        
        do! botClient.DeleteMessageAsync(ChatId(message.Chat.Id), message.MessageId)
        let probablyChannelName =
            if message.SenderChat <> null then
                message.SenderChat.Title
            else
                "[unknown]"
        logger.LogInformation $"Deleted message from channel {probablyChannelName}"

    // check if message is a known command
    // and check that user is allowed to ban others
    elif isBanOnReplyMessage message && isBanAuthorized botConfig message logger then

        // delete command message
        let deleteCmdTask = botClient.DeleteMessageAsync(ChatId(message.Chat.Id), message.MessageId)
        // delete message that was replied to
        let deleteReplyTask = botClient.DeleteMessageAsync(ChatId(message.Chat.Id), message.ReplyToMessage.MessageId)
        // update user in DB
        let banUserInDb =
            message.ReplyToMessage.From
            |> DbUser.newUser
            |> DbUser.banUser message.From.Id (Option.ofObj message.ReplyToMessage.Text)
            |> DB.upsertUser
            
        let deletedUserMessagesTask = task {
            let fromUserId = message.ReplyToMessage.From.Id
            
            // delete all recorded messages from user in all chats
            let! allUserMessages = DB.getUserMessages fromUserId
            for msg in allUserMessages do
                // try to delete each message separately
                try
                    do! botClient.DeleteMessageAsync(ChatId(msg.Chat_Id), msg.Message_Id)
                with e ->
                    logger.LogError ($"Failed to delete message {msg.Message_Id} from chat {msg.Chat_Id}", e)
                
            // delete recorded messages from DB
            return! DB.deleteUserMessages fromUserId
        }
        
        // try ban user in all monitored chats
        let! banResults = banInAllChats botConfig botClient message.ReplyToMessage.From.Id
        let! deletedUserMessages = deletedUserMessagesTask
        
        // produce aggregated log message
        let logMsg = aggregateBanResultInLogMsg logger message deletedUserMessages banResults 

        // log both to logger and to logs channel
        let! _ = botClient.SendTextMessageAsync(ChatId(botConfig.LogsChannelId), logMsg)
        logger.LogInformation logMsg
        
        let! _ = banUserInDb
        do! deleteCmdTask
        do! deleteReplyTask
        
    // ping command for testing that bot works and you can talk to it
    elif isPingCommand message && isMessageFromAdmin botConfig message then
        // delete command message
        let deleteCmdTask = botClient.DeleteMessageAsync(ChatId(message.Chat.Id), message.MessageId)
        let! _ = botClient.SendTextMessageAsync(ChatId(message.Chat.Id), "pong")
        do! deleteCmdTask
        
    // if message is not a command, just save it ID to DB
    else
        let! _ =
            message
            |> DbMessage.newMessage
            |> DB.insertMessage
        ()
}
