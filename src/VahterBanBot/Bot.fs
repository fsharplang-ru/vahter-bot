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
open VahterBanBot.Antispam

let botActivity = new ActivitySource("VahterBanBot")

let isChannelMessage (message: Message) =
    message.From.IsBot &&
    message.From.FirstName = "Channel" &&
    message.From.Username = "Channel_Bot"
    
let isPingCommand (message: Message) =
    message.Text = "/ban ping"

let isBanCommand (message: Message) =
    message.Text = "/ban"
    
let isUnbanCommand (message: Message) =
    message.Text.StartsWith "/unban "

let isSoftBanCommand (message: Message) =
    message.Text.StartsWith "/sban"

let isSoftBanOnReplyCommand (message: Message) =
    isSoftBanCommand message &&
    message.ReplyToMessage <> null

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
    message.Text <> null &&
    (isPingCommand message ||
     isBanCommand message ||
     isUnbanCommand message ||
     isSoftBanCommand message)

let isBanAuthorized
    (botConfig: BotConfiguration)
    (message: Message)
    (logger: ILogger)
    (targetUserId: int64)
    (targetUsername: string option)
    (isBan: bool)
    =
    let banType = if isBan then "ban" else "unban"
    let fromUserId = message.From.Id
    let fromUsername = message.From.Username
    let chatId = message.Chat.Id
    let chatUsername = message.Chat.Username
    
    // check that user is allowed to ban others
    if isMessageFromAdmin botConfig message then
        if not(isMessageFromAllowedChats botConfig message) then
            logger.LogWarning $"User {fromUsername} {fromUserId} tried to {banType} user {targetUsername} ({targetUserId}) from not allowed chat {chatUsername} ({chatId})"
            false
        // check that user is not trying to ban other admins
        elif isBan && isBannedPersonAdmin botConfig message then
            logger.LogWarning $"User {fromUsername} ({fromUserId}) tried to {banType} admin {targetUsername} ({targetUserId}) in chat {chatUsername} ({chatId}"
            false
        else
            true
    else
        logger.LogWarning $"User {fromUsername} ({fromUserId}) tried to {banType} user {targetUsername} ({targetUserId}) without being admin in chat {chatUsername} ({chatId}"
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

let softBanInChat (botClient: ITelegramBotClient) (chatId: ChatId) targetUserId (duration: int) = task {
    let permissions = ChatPermissions(
        CanSendMessages = false,
        CanSendAudios = false,
        CanSendDocuments = false,
        CanSendPhotos = false,
        CanSendVideos = false,
        CanSendVideoNotes = false,
        CanSendVoiceNotes = false,
        CanSendPolls = false,
        CanSendOtherMessages = false,
        CanAddWebPagePreviews = false,
        CanChangeInfo = false,
        CanInviteUsers = false,
        CanPinMessages = false,
        CanManageTopics = false
        )
    let untilDate = DateTime.Now.AddHours duration
    try 
        do! botClient.RestrictChatMemberAsync(chatId, targetUserId, permissions, Nullable(), untilDate)
        return Ok(chatId, targetUserId)
    with e ->
        return Error(chatId, targetUserId, e)
}

let unbanInAllChats (botConfig: BotConfiguration) (botClient: ITelegramBotClient) targetUserId = task {
    let banTasks =
        botConfig.ChatsToMonitor
        |> Seq.map (fun (KeyValue(chatUserName, chatId)) -> task {
            // unban user in each chat
            try
                do! botClient.UnbanChatMemberAsync(ChatId chatId, targetUserId, true)
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

let aggregateResultInLogMsg
    (isBan: bool)
    (message: Message)
    (targetUserId: int64)
    (targetUserName: string option)
    (logger: ILogger)
    (deletedUserMessages: int) // 0 for unban
    (results: Result<string * int64, string * int64 * exn> []) =

    let resultType = if isBan then "ban" else "unban"
    let sanitizedUsername =
        targetUserName
        |> Option.map prependUsername
        |> Option.defaultValue "{NO_USERNAME}"

    let vahterUserId = message.From.Id
    let vahterUsername = message.From.Username
    let chatName = message.Chat.Username
    let chatId = message.Chat.Id
    
    let logMsgBuilder = StringBuilder()
    %logMsgBuilder.Append($"Vahter {prependUsername vahterUsername}({vahterUserId}) {resultType}ned {sanitizedUsername} ({targetUserId}) in {prependUsername chatName}({chatId})")
    
    // we don't want to spam logs channel if all is good
    let allChatsOk = results |> Array.forall Result.isOk
    if allChatsOk then
        %logMsgBuilder.AppendLine " in all chats"
        if isBan then
            %logMsgBuilder.AppendLine $"Deleted {deletedUserMessages} messages"
    else
        if isBan then
            %logMsgBuilder.AppendLine ""
            %logMsgBuilder.AppendLine $"Deleted {deletedUserMessages} messages in chats:"

        (logMsgBuilder, results)
        ||> Array.fold (fun (sb: StringBuilder) result ->
            match result with
            | Ok (chatUsername, chatId) ->
                sb.AppendLine($"{prependUsername chatUsername} ({chatId}) - OK")
            | Error (chatUsername, chatId, e) ->
                logger.LogError($"Failed to {resultType} user {sanitizedUsername} ({targetUserId}) in chat {prependUsername chatUsername} ({chatId})", e)
                sb.AppendLine($"{prependUsername chatUsername} ({chatId}) - FAILED. {e.Message}")
        ) |> ignore
    string logMsgBuilder

let aggregateBanResultInLogMsg message =
    aggregateResultInLogMsg
        true
        message
        message.ReplyToMessage.From.Id
        (Some message.ReplyToMessage.From.Username)

let aggregateUnbanResultInLogMsg message targetUserId targetUsername =
    aggregateResultInLogMsg
        false
        message
        targetUserId
        targetUsername

let softBanResultInLogMsg (message: Message) (duration: int) =
    let logMsgBuilder = StringBuilder()
    let untilDate = (DateTime.Now.AddHours duration).ToString "u"
    %logMsgBuilder.Append $"Vahter {prependUsername message.From.Username}({message.From.Id}) "
    %logMsgBuilder.Append $"softbanned {prependUsername message.ReplyToMessage.From.Username}({message.ReplyToMessage.From.Id}) "
    %logMsgBuilder.Append $"in {prependUsername message.Chat.Username}({message.Chat.Id}) "
    %logMsgBuilder.Append $"until {untilDate}"
    string logMsgBuilder


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
    let logMsg = aggregateBanResultInLogMsg message logger deletedUserMessages banResults 

    // log both to logger and to logs channel
    do! botClient.SendTextMessageAsync(ChatId(botConfig.LogsChannelId), logMsg) |> taskIgnore
    logger.LogInformation logMsg
    
    do! banUserInDb.Ignore()
    do! deleteReplyTask
}

let softBanOnReply
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (message: Message)
    (logger: ILogger) = task {
        use banOnReplyActivity = botActivity.StartActivity("softBanOnReply")
        %banOnReplyActivity
            .SetTag("vahterId", message.From.Id)
            .SetTag("vahterUsername", message.From.Username)
            .SetTag("targetId", message.ReplyToMessage.From.Id)
            .SetTag("targetUsername", message.ReplyToMessage.From.Username)
        
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
        
        let maybeDurationString = message.Text.Split " " |> Seq.last
        // use last value as soft ban duration
        let duration =
            match Int32.TryParse maybeDurationString with
            | true, x -> x
            | _ -> 24 // 1 day should be enough

        let logText = softBanResultInLogMsg message duration
        
        do! softBanInChat botClient (ChatId message.Chat.Id) message.ReplyToMessage.From.Id duration |> taskIgnore
        do! deleteReplyTask
        
        do! botClient.SendTextMessageAsync(ChatId(botConfig.LogsChannelId), logText) |> taskIgnore
        logger.LogInformation logText
}

let unban
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (message: Message)
    (logger: ILogger)
    (targetUserId: int64) = task {
    use banOnReplyActivity = botActivity.StartActivity("unban")
    %banOnReplyActivity
        .SetTag("vahterId", message.From.Id)
        .SetTag("vahterUsername", message.From.Username)
        .SetTag("targetId", targetUserId)

    let! user = DB.getUserById targetUserId
    let unbanUserTask = task {
        if user.IsSome then
            %banOnReplyActivity.SetTag("targetUsername", user.Value.Username)
            let! unbannedUser =
                user.Value
                |> DbUser.unban
                |> DB.upsertUser
            return Some unbannedUser
        else
            return None
    }
    let targetUsername = user |> Option.bind (fun u -> u.Username)

    // try unban user in all monitored chats
    let! unbanResults = unbanInAllChats botConfig botClient targetUserId
    
    // produce aggregated log message
    let logMsg = aggregateUnbanResultInLogMsg message targetUserId targetUsername logger 0 unbanResults

    // log both to logger and to logs channel
    do! botClient.SendTextMessageAsync(ChatId(botConfig.LogsChannelId), logMsg) |> taskIgnore
    logger.LogInformation logMsg
    
    do! unbanUserTask.Ignore()
}

let warnSpamDetection
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (message: Message)
    (logger: ILogger)
    score = task {
    use banOnReplyActivity = botActivity.StartActivity("warnSpamDetection")
    %banOnReplyActivity
        .SetTag("spammerId", message.From.Id)
        .SetTag("spammerUsername", message.From.Username)

    let logMsg = $"Detected spam (score: {score}) in {prependUsername message.Chat.Username} ({message.Chat.Id}) from {prependUsername message.From.Username} ({message.From.Id}) with text:\n{message.Text}"
    
    // log both to logger and to logs channel
    do! botClient.SendTextMessageAsync(ChatId(botConfig.LogsChannelId), logMsg) |> taskIgnore
    logger.LogInformation logMsg
}

let onUpdate
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (message: Message) = task {
    
    use banOnReplyActivity = botActivity.StartActivity("onUpdate")

    // early return if if we can't process it
    if isNull message || isNull message.From then
        logger.LogWarning "Received update without message"
    else

    %banOnReplyActivity
        .SetTag("chatId", message.Chat.Id)
        .SetTag("chatUsername", message.Chat.Username)

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
        // check that user is allowed to (un)ban others
        if isBanOnReplyCommand message then
            let targetUserId = message.ReplyToMessage.From.Id
            let targetUsername = Option.ofObj message.ReplyToMessage.From.Username
            let authed =
                isBanAuthorized
                    botConfig
                    message
                    logger
                    targetUserId
                    targetUsername
                    true
            if authed then
                do! banOnReply botClient botConfig message logger
        elif isUnbanCommand message then
            let targetUserId = message.Text.Split(" ", StringSplitOptions.RemoveEmptyEntries)[1] |> int64
            let authed =
                isBanAuthorized
                    botConfig
                    message
                    logger
                    targetUserId
                    None
                    false
            if authed then
                do! unban botClient botConfig message logger targetUserId
        elif isSoftBanOnReplyCommand message then
            let targetUserId = message.ReplyToMessage.From.Id
            let targetUsername = Option.ofObj message.ReplyToMessage.From.Username
            let authed =
                isBanAuthorized
                    botConfig
                    message
                    logger
                    targetUserId
                    targetUsername
                    true
            if authed then
                do! softBanOnReply botClient botConfig message logger
        // ping command for testing that bot works and you can talk to it
        elif isPingCommand message then
            do! ping botClient message
        do! deleteCmdTask

    // if message is not a command from authorized user, just save it ID to DB
    else
        let spamScore = if message.Text <> null then calcSpamScore message.Text else 0
        
        if spamScore >= 100 then
            do! warnSpamDetection botClient botConfig message logger spamScore    
        
        use _ =
            botActivity
                .StartActivity("justMessage")
                .SetTag("fromUserId", message.From.Id)
                .SetTag("fromUsername", message.From.Username)
                .SetTag("spamScore", spamScore)
        do!
            message
            |> DbMessage.newMessage
            |> DB.insertMessage
            |> taskIgnore
}
