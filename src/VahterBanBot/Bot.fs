module VahterBanBot.Bot

open System
open System.Diagnostics
open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open VahterBanBot.Types
open VahterBanBot.Utils

let botActivity = new ActivitySource("VahterBanBot")

let isChannelMessage (message: Message) =
    message.From.IsBot &&
    message.From.FirstName = "Channel" &&
    message.From.Username = "Channel_Bot"
    
let isPingCommand (message: Message) =
    message.Text = "/ban ping"

let isBanCommand (message: Message) =
    message.Text = "/ban"

let isBanOnReplyCommand (message: Message) =
    isBanCommand message &&
    message.ReplyToMessage <> null

let isMessageFromAllowedChats (botConfig: BotConfiguration) (message: Message) =
    botConfig.ChatsToMonitor.ContainsValue message.Chat.Id
    
let isMessageFromAdmin (botConfig: BotConfiguration) (message: Message) =
    botConfig.AllowedUsers.ContainsValue message.From.Id

let isBannedPersonAdmin (botConfig: BotConfiguration) (message: Message) =
    botConfig.AllowedUsers.ContainsValue message.ReplyToMessage.From.Id

let isKnownCommand (message: Message) =
    isPingCommand message ||
    isBanCommand message

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

let safeTaskAwait onError (task: Task) =
    task.ContinueWith(fun (t: Task) ->
        if t.IsFaulted then
            onError t.Exception
    )

let aggregateBanResultInLogMsg
    (logger: ILogger)
    (message: Message)
    (deletedUserMessages: int)
    (banResults: Result<string * int64, string * int64 * exn> []) =

    let vahterUserId = message.From.Id
    let vahterUsername = message.From.Username
    let chatName = message.Chat.Username
    let chatId = message.Chat.Id
    
    let targetUserId = message.ReplyToMessage.From.Id
    let targetUsername = message.ReplyToMessage.From.Username
    let logMsgBuilder = StringBuilder()
    %logMsgBuilder.Append($"Vahter {prependUsername vahterUsername}({vahterUserId}) banned {prependUsername targetUsername} ({targetUserId}) in {prependUsername chatName}({chatId})")
    
    // we don't want to spam logs channel if all is good
    let allChatsOk = banResults |> Array.forall Result.isOk
    if allChatsOk then
        %logMsgBuilder.AppendLine " in all chats"
        logMsgBuilder.AppendLine $"Deleted {deletedUserMessages} messages"
        |> string
    else
        
        %logMsgBuilder.AppendLine ""
        %logMsgBuilder.AppendLine $"Deleted {deletedUserMessages} messages in chats:"

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

let ping
    (botClient: ITelegramBotClient)
    (message: Message) = task {
    use _ = botActivity.StartActivity("ping")
    do! botClient.SendTextMessageAsync(ChatId(message.Chat.Id), "pong") |> taskIgnore
}

let deleteChannelMessage
    (botClient: ITelegramBotClient)
    (message: Message)
    (logger: ILogger) = task {
    use banOnReplyActivity = botActivity.StartActivity("deleteChannelMessage")
    do! botClient.DeleteMessageAsync(ChatId(message.Chat.Id), message.MessageId)
        |> safeTaskAwait (fun e -> logger.LogError ($"Failed to delete message {message.MessageId} from chat {message.Chat.Id}", e))

    let probablyChannelName =
        if message.SenderChat <> null then
            message.SenderChat.Title
        else
            "[unknown]"
    %banOnReplyActivity.SetTag("channelName", probablyChannelName)
    logger.LogInformation $"Deleted message from channel {probablyChannelName}"
}

let banOnReply
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (message: Message)
    (logger: ILogger) = task {
    use banOnReplyActivity = botActivity.StartActivity("banOnReply")
    %banOnReplyActivity
        .SetTag("vahterId", message.From.Id)
        .SetTag("vahterUsername", message.From.Username)
        .SetTag("targetId", message.ReplyToMessage.From.Id)
        .SetTag("targetUsername", message.ReplyToMessage.From.Username)

    // delete message that was replied to
    let deleteReplyTask = task {
        use _ =
            botActivity
                .StartActivity("deleteReplyMsg")
                .SetTag("msgId", message.ReplyToMessage.MessageId)
                .SetTag("chatId", message.Chat.Id)
                .SetTag("chatUsername", message.Chat.Username)
        do! botClient.DeleteMessageAsync(ChatId(message.Chat.Id), message.ReplyToMessage.MessageId)
            |> safeTaskAwait (fun e -> logger.LogError ($"Failed to delete reply message {message.ReplyToMessage.MessageId} from chat {message.Chat.Id}", e))
    }
    // update user in DB
    let banUserInDb =
        message.ReplyToMessage.From
        |> DbUser.newUser
        |> DbUser.banUser message.From.Id (Option.ofObj message.ReplyToMessage.Text)
        |> DB.upsertUser

    let deletedUserMessagesTask = task {
        let fromUserId = message.ReplyToMessage.From.Id
        let! allUserMessages = DB.getUserMessages fromUserId
        logger.LogInformation($"Deleting {allUserMessages.Length} messages from user {fromUserId}")
        
        // delete all recorded messages from user in all chats
        do!
            allUserMessages
            |> Seq.map (fun msg -> task {
                try
                    use _ =
                        botActivity
                            .StartActivity("deleteMsg")
                            .SetTag("msgId", msg.Message_Id)
                            .SetTag("chatId", msg.Chat_Id)
                    do! botClient.DeleteMessageAsync(ChatId(msg.Chat_Id), msg.Message_Id)
                with e ->
                    logger.LogError ($"Failed to delete message {msg.Message_Id} from chat {msg.Chat_Id}", e)
            })
            |> Task.WhenAll
            |> taskIgnore

        // delete recorded messages from DB
        return! DB.deleteMsgs allUserMessages
    }
    
    // try ban user in all monitored chats
    let! banResults = banInAllChats botConfig botClient message.ReplyToMessage.From.Id
    let! deletedUserMessages = deletedUserMessagesTask
    
    // produce aggregated log message
    let logMsg = aggregateBanResultInLogMsg logger message deletedUserMessages banResults 

    // log both to logger and to logs channel
    do! botClient.SendTextMessageAsync(ChatId(botConfig.LogsChannelId), logMsg) |> taskIgnore
    logger.LogInformation logMsg
    
    do! banUserInDb.Ignore()
    do! deleteReplyTask
}

let onUpdate
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (message: Message) = task {
    
    use banOnReplyActivity = botActivity.StartActivity("onUpdate")
    %banOnReplyActivity
        .SetTag("chatId", message.Chat.Id)
        .SetTag("chatUsername", message.Chat.Username)

    // early return if if we can't process it
    if isNull message || isNull message.From then
        logger.LogWarning "Received update without message"
    else

    // upserting user to DB
    let! _ =
        DbUser.newUser message.From
        |> DB.upsertUser
        |> taskIgnore

    // check if message comes from channel, we should delete it immediately
    if botConfig.ShouldDeleteChannelMessages && isChannelMessage message then
        do! deleteChannelMessage botClient message logger

    // check if message is a known command from authorized user
    elif isKnownCommand message && isMessageFromAdmin botConfig message then
        use _ = botActivity.StartActivity("adminCommand")
        // delete command message
        let deleteCmdTask = task {
            use _ = 
                botActivity
                    .StartActivity("deleteCmdMsg")
                    .SetTag("msgId", message.MessageId)
                    .SetTag("chatId", message.Chat.Id)
                    .SetTag("chatUsername", message.Chat.Username)
            do! botClient.DeleteMessageAsync(ChatId(message.Chat.Id), message.MessageId)
                |> safeTaskAwait (fun e -> logger.LogError ($"Failed to delete ping message {message.MessageId} from chat {message.Chat.Id}", e))
        }
        // check that user is allowed to ban others
        if isBanOnReplyCommand message && isBanAuthorized botConfig message logger then
            do! banOnReply botClient botConfig message logger

        // ping command for testing that bot works and you can talk to it
        elif isPingCommand message then
            do! ping botClient message
        do! deleteCmdTask

    // if message is not a command from authorized user, just save it ID to DB
    else
        use _ =
            botActivity
                .StartActivity("justMessage")
                .SetTag("fromUserId", message.From.Id)
                .SetTag("fromUsername", message.From.Username)
        do!
            message
            |> DbMessage.newMessage
            |> DB.insertMessage
            |> taskIgnore
}
