module VahterBanBot.Bot

open System
open System.Diagnostics
open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open Telegram.Bot.Types.ReplyMarkups
open VahterBanBot.ML
open VahterBanBot.ComputerVision
open VahterBanBot.Types
open VahterBanBot.Utils
open VahterBanBot.UpdateChatAdmins
open VahterBanBot.Metrics

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
    
let isUserVahter (botConfig: BotConfiguration) (user: DbUser) =
    botConfig.AllowedUsers.ContainsValue user.id

let isBannedPersonAdmin (botConfig: BotConfiguration) (message: Message) =
    botConfig.AllowedUsers.ContainsValue message.From.Id

let isKnownCommand (message: Message) =
    message.Text <> null &&
    (isPingCommand message ||
     isBanCommand message ||
     isUnbanCommand message ||
     isSoftBanCommand message)

let isBanAuthorized
    (botConfig: BotConfiguration)
    (bannedMessage: Message)
    (vahter: DbUser)
    (logger: ILogger) =
    let fromUserId = vahter.id
    let fromUsername = defaultArg vahter.username null
    let chatId = bannedMessage.Chat.Id
    let chatUsername = bannedMessage.Chat.Username
    let targetUserId = bannedMessage.From.Id
    let targetUsername = bannedMessage.From.Username
    
    // check that user is allowed to ban others
    if isUserVahter botConfig vahter then
        if not(isMessageFromAllowedChats botConfig bannedMessage) then
            logger.LogWarning $"User {fromUsername} {fromUserId} tried to ban user {prependUsername targetUsername} ({targetUserId}) from not allowed chat {chatUsername} ({chatId})"
            false
        // check that user is not trying to ban other admins
        elif isBannedPersonAdmin botConfig bannedMessage then
            logger.LogWarning $"User {fromUsername} ({fromUserId}) tried to ban admin {prependUsername targetUsername} ({targetUserId}) in chat {chatUsername} ({chatId}"
            false
        else
            true
    else
        logger.LogWarning $"User {fromUsername} ({fromUserId}) tried to ban user {prependUsername targetUsername} ({targetUserId}) without being admin in chat {chatUsername} ({chatId}"
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
    let untilDate = DateTime.UtcNow.AddHours duration
    try 
        do! botClient.RestrictChatMemberAsync(chatId, targetUserId, permissions, untilDate = untilDate)
        return Ok(chatId, targetUserId)
    with e ->
        return Error(chatId, targetUserId, e)
}

let unbanInAllChats (botConfig: BotConfiguration) (botClient: ITelegramBotClient) targetUserId = task {
    let unbanTasks =
        botConfig.ChatsToMonitor
        |> Seq.map (fun (KeyValue(chatUserName, chatId)) -> task {
            // unban user in each chat
            try
                do! botClient.UnbanChatMemberAsync(ChatId chatId, targetUserId, true)
                return Ok(chatUserName, chatId) 
            with e ->
                return Error (chatUserName, chatId, e)
        })
    return! Task.WhenAll unbanTasks
}

let safeTaskAwait onError (task: Task) =
    task.ContinueWith(fun (t: Task) ->
        if t.IsFaulted then
            onError t.Exception
    )

let aggregateResultInLogMsg
    (isBan: bool)
    (chat: Chat)
    (vahter: DbUser)
    (user: DbUser)

    (logger: ILogger)
    (deletedUserMessages: int) // 0 for unban
    (results: Result<string * int64, string * int64 * exn> []) =

    let resultType = if isBan then "ban" else "unban"
    let sanitizedUsername = defaultArg user.username null |> prependUsername
    let targetUserId = user.id
    let vahterUsername = defaultArg vahter.username null |> prependUsername
    let vahterUserId = vahter.id

    let chatName = chat.Username
    let chatId = chat.Id
    
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

let aggregateBanResultInLogMsg chat vahter user =
    aggregateResultInLogMsg
        true
        chat
        vahter
        user

let aggregateUnbanResultInLogMsg chat vahter user =
    aggregateResultInLogMsg
        false
        chat
        vahter
        user

let softBanResultInLogMsg (message: Message) (vahter: DbUser) (duration: int) =
    let logMsgBuilder = StringBuilder()
    let vahterUsername = defaultArg vahter.username null
    let untilDate = (DateTime.UtcNow.AddHours duration).ToString "u"
    %logMsgBuilder.Append $"Vahter {prependUsername vahterUsername}({vahter.id}) "
    %logMsgBuilder.Append $"softbanned {prependUsername message.From.Username}({message.From.Id}) "
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
    recordDeletedMessage message.Chat.Id message.Chat.Username "channelMessage"
    do! botClient.DeleteMessageAsync(ChatId(message.Chat.Id), message.MessageId)
        |> safeTaskAwait (fun e -> logger.LogWarning ($"Failed to delete message {message.MessageId} from chat {message.Chat.Id}", e))

    let probablyChannelName =
        if message.SenderChat <> null then
            message.SenderChat.Title
        else
            "[unknown]"
    %banOnReplyActivity.SetTag("channelName", probablyChannelName)
    logger.LogInformation $"Deleted message from channel {probablyChannelName}"
}

let totalBan
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (message: Message)
    (vahter: DbUser)
    (logger: ILogger) = task {
    use banOnReplyActivity = botActivity.StartActivity("totalBan")
    %banOnReplyActivity
        .SetTag("vahterId", vahter.id)
        .SetTag("vahterUsername", (defaultArg vahter.username null))
        .SetTag("targetId", message.From.Id)
        .SetTag("targetUsername", message.From.Username)
        
    // delete message
    let deleteMsgTask = task {
        use _ =
            botActivity
                .StartActivity("deleteMsg")
                .SetTag("msgId", message.MessageId)
                .SetTag("chatId", message.Chat.Id)
                .SetTag("chatUsername", message.Chat.Username)
        recordDeletedMessage message.Chat.Id message.Chat.Username "totalBan_initial"
        do! botClient.DeleteMessageAsync(ChatId(message.Chat.Id), message.MessageId)
            |> safeTaskAwait (fun e -> logger.LogWarning ($"Failed to delete message {message.MessageId} from chat {message.Chat.Id}", e))
    }

    // update user in DB
    let! updatedUser =
        message.From
        |> DbUser.newUser
        |> DB.upsertUser
        
    let deletedUserMessagesTask = task {
        let fromUserId = message.From.Id
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
                            .SetTag("msgId", msg.message_id)
                            .SetTag("chatId", msg.chat_id)
                    recordDeletedMessage msg.chat_id null "totalBan_history"
                    do! botClient.DeleteMessageAsync(ChatId(msg.chat_id), msg.message_id)
                with e ->
                    logger.LogWarning ($"Failed to delete message {msg.message_id} from chat {msg.chat_id}", e)
            })
            |> Task.WhenAll
            |> taskIgnore

        return allUserMessages.Length
    }
    
    // Clean up ALL callbacks for this user (they may have multiple spam messages)
    // This ensures empty inbox when user is banned
    let cleanupCallbacksTask = task {
        let! userCallbacks = DB.getCallbacksByUserId message.From.Id
        logger.LogInformation($"Cleaning up {userCallbacks.Length} callbacks for banned user {message.From.Id}")
        
        do!
            userCallbacks
            |> Seq.map (fun callback -> task {
                // Delete message from action channel
                match callback.action_message_id with
                | Some msgId ->
                    do! botClient.DeleteMessageAsync(ChatId(botConfig.ActionChannelId), msgId)
                        |> safeTaskAwait (fun e -> logger.LogWarning ($"Failed to delete callback message {msgId} from action channel", e))
                | None -> ()
                // Delete callback from DB
                do! DB.deleteCallback callback.id
            })
            |> Task.WhenAll
            |> taskIgnore
    }
    
    // try ban user in all monitored chats
    let! banResults = banInAllChats botConfig botClient message.From.Id
    let! deletedUserMessages = deletedUserMessagesTask
    do! cleanupCallbacksTask
    
    // produce aggregated log message
    let logMsg = aggregateBanResultInLogMsg message.Chat vahter updatedUser logger deletedUserMessages banResults

    // metrics: count banned user per vahter for successful bans
    let vahterUsername = defaultArg vahter.username null
    bannedUsersCounter.Add(1L, tagsForVahter vahter.id vahterUsername)
    
    // add ban record to DB
    do! message
        |> DbBanned.banMessage vahter.id
        |> DB.banUser

    // log both to logger and to All Logs topic
    do! botClient.SendTextMessageAsync(
            chatId = ChatId(botConfig.ActionChannelId),
            text = logMsg,
            messageThreadId = botConfig.ActionAllLogsTopicId
        ) |> taskIgnore
    logger.LogInformation logMsg

    do! deleteMsgTask
}

let banOnReply
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (message: Message)
    (vahter: DbUser)
    (logger: ILogger) = task {
    use banOnReplyActivity = botActivity.StartActivity("banOnReply")
    %banOnReplyActivity
        .SetTag("vahterId", message.From.Id)
        .SetTag("vahterUsername", message.From.Username)
        .SetTag("targetId", message.ReplyToMessage.From.Id)
        .SetTag("targetUsername", message.ReplyToMessage.From.Username)
    
    let targetMsg = message.ReplyToMessage
    
    // Try to record action (race condition protection between /ban and KILL button)
    let! actionRecorded = DB.tryRecordVahterAction 
                            vahter.id "manual_ban" targetMsg.From.Id 
                            targetMsg.Chat.Id targetMsg.MessageId
    
    if actionRecorded then
        %banOnReplyActivity.SetTag("actionRecorded", true)
        do! totalBan
                botClient
                botConfig
                targetMsg
                vahter
                logger
    else
        %banOnReplyActivity.SetTag("actionRecorded", false)
        logger.LogInformation $"User {targetMsg.From.Id} already banned by another vahter"
}

let softBanMsg
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (commandMessage: Message)
    (vahter: DbUser)
    (logger: ILogger) = task {
        let messageToRemove = commandMessage.ReplyToMessage
        
        use banOnReplyActivity = botActivity.StartActivity("softBanOnReply")
        %banOnReplyActivity
            .SetTag("vahterId", vahter.id)
            .SetTag("vahterUsername", defaultArg vahter.username null)
            .SetTag("targetId", messageToRemove.From.Id)
            .SetTag("targetUsername", messageToRemove.From.Username)
        
        let deleteMsgTask = task {
            use _ =
                botActivity
                    .StartActivity("deleteMsg")
                    .SetTag("msgId", messageToRemove.MessageId)
                    .SetTag("chatId", messageToRemove.Chat.Id)
                    .SetTag("chatUsername", messageToRemove.Chat.Username)
            recordDeletedMessage messageToRemove.Chat.Id messageToRemove.Chat.Username "softBan"
            do! botClient.DeleteMessageAsync(ChatId(messageToRemove.Chat.Id), messageToRemove.MessageId)
                |> safeTaskAwait (fun e -> logger.LogWarning ($"Failed to delete reply message {messageToRemove.MessageId} from chat {messageToRemove.Chat.Id}", e))
        }
        
        let maybeDurationString = commandMessage.Text.Split " " |> Seq.last
        // use last value as soft ban duration
        let duration =
            match Int32.TryParse maybeDurationString with
            | true, x -> x
            | _ -> 24 // 1 day should be enough

        let logText = softBanResultInLogMsg messageToRemove vahter duration
        
        do! softBanInChat botClient (ChatId messageToRemove.Chat.Id) messageToRemove.From.Id duration |> taskIgnore
        do! deleteMsgTask
        
        do! botClient.SendTextMessageAsync(
                chatId = ChatId(botConfig.ActionChannelId),
                text = logText,
                messageThreadId = botConfig.ActionAllLogsTopicId
            ) |> taskIgnore
        logger.LogInformation logText
}

let unban
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (message: Message)
    (vahter: DbUser)
    (userToUnban: DbUser)
    (logger: ILogger) = task {
    use banOnReplyActivity = botActivity.StartActivity("unban")
    let targetUserId = userToUnban.id
    %banOnReplyActivity
        .SetTag("vahterId", vahter.id)
        .SetTag("vahterUsername", defaultArg vahter.username null)
        .SetTag("targetId", targetUserId)
        .SetTag("targetUsername", userToUnban.username)
        
    // delete ban record from DB
    do! userToUnban.id
        |> DB.unbanUser 

    // try unban user in all monitored chats
    let! unbanResults = unbanInAllChats botConfig botClient targetUserId
    
    // produce aggregated log message
    let logMsg = aggregateUnbanResultInLogMsg message.Chat vahter userToUnban logger 0 unbanResults

    // log both to logger and to All Logs topic
    do! botClient.SendTextMessageAsync(
            chatId = ChatId(botConfig.ActionChannelId),
            text = logMsg,
            messageThreadId = botConfig.ActionAllLogsTopicId
        ) |> taskIgnore
    logger.LogInformation logMsg
}

let killSpammerAutomated
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (message: Message)
    (logger: ILogger)
    (deleteMessage: bool)
    score = task {
    use banOnReplyActivity = botActivity.StartActivity("killAutomated")
    %banOnReplyActivity
        .SetTag("spammerId", message.From.Id)
        .SetTag("spammerUsername", message.From.Username)
        
    if deleteMessage then
        // delete message
        recordDeletedMessage message.Chat.Id message.Chat.Username "spamDeletion"
        do! botClient.DeleteMessageAsync(ChatId(message.Chat.Id), message.MessageId)
            |> safeTaskAwait (fun e -> logger.LogWarning ($"Failed to delete message {message.MessageId} from chat {message.Chat.Id}", e))
        // 0 here is the bot itself
        do! DbBanned.banMessage 0 message
            |> DB.banUserByBot

    let msgType = if deleteMessage then "Deleted" else "Detected"
    let logMsg = $"{msgType} spam (score: {score}) in {prependUsername message.Chat.Username} ({message.Chat.Id}) from {prependUsername message.From.Username} ({message.From.Id}) with text:\n{message.TextOrCaption}"

    // Determine which topic and what buttons
    let topicId, replyMarkup =
        if deleteMessage then
            // Detected spam → Detected topic with NOT A SPAM button
            botConfig.ActionDetectedTopicId,
            fun (callbackId: Guid) -> InlineKeyboardMarkup [
                InlineKeyboardButton.WithCallbackData("✅ NOT a spam", string callbackId)
            ]
        else
            // Potential spam → Potential topic with KILL and NOT SPAM buttons
            botConfig.ActionPotentialTopicId,
            fun (callbackId: Guid) -> InlineKeyboardMarkup [|
                InlineKeyboardButton.WithCallbackData("🚫 KILL", string callbackId);
                InlineKeyboardButton.WithCallbackData("✅ NOT SPAM", string callbackId)
            |]

    // Phase 1: Create callback without message_id
    let callbackData = 
        if deleteMessage then CallbackMessage.NotASpam { message = message }
        else CallbackMessage.Spam { message = message }
    let! callback = DB.newCallbackPending callbackData message.From.Id topicId
    
    // Phase 2: Post to action channel topic
    let! sent = botClient.SendTextMessageAsync(
        chatId = ChatId(botConfig.ActionChannelId),
        text = logMsg,
        messageThreadId = topicId,
        replyMarkup = replyMarkup callback.id
    )
    
    // Phase 3: Update callback with message_id
    do! DB.updateCallbackMessageId callback.id sent.MessageId
    
    // Send to All Logs topic (readonly, no buttons)
    do! botClient.SendTextMessageAsync(
            chatId = ChatId(botConfig.ActionChannelId),
            text = logMsg,
            messageThreadId = botConfig.ActionAllLogsTopicId
        ) |> taskIgnore
    
    logger.LogInformation logMsg
}

let autoBan
    (botUser: DbUser)
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (message: Message)
    (logger: ILogger) = task {
    use banOnReplyActivity = botActivity.StartActivity("autoBan")
    %banOnReplyActivity
        .SetTag("spammerId", message.From.Id)
        .SetTag("spammerUsername", message.From.Username)
    
    let! userStats = DB.getUserStatsByLastNMessages botConfig.MlSpamAutobanCheckLastMsgCount message.From.Id
    let socialScore = userStats.good - userStats.bad
    
    %banOnReplyActivity.SetTag("socialScore", socialScore)
    
    if double socialScore <= botConfig.MlSpamAutobanScoreThreshold then
        // ban user in all monitored chats
        do! totalBan botClient botConfig message botUser logger
        let msg = $"Auto-banned user {prependUsername message.From.Username} ({message.From.Id}) due to the low social score {socialScore}"
        logger.LogInformation msg
        do! botClient.SendTextMessageAsync(
                chatId = ChatId(botConfig.ActionChannelId),
                text = msg,
                messageThreadId = botConfig.ActionAllLogsTopicId
            ) |> taskIgnore
}

let justMessage
    (botUser: DbUser)
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (ml: MachineLearning)
    (message: Message) = task {

    use _ =
        botActivity
            .StartActivity("justMessage")
            .SetTag("fromUserId", message.From.Id)
            .SetTag("fromUsername", message.From.Username)

    // check if user got auto-banned already
    // that could happen due to the race condition between spammers mass messages
    // and the bot's processing queue
    let! isAutoBanned = DB.isBannedByVahter botUser.id message.From.Id 
    if isAutoBanned then
        // just delete message and move on
        recordDeletedMessage message.Chat.Id message.Chat.Username "alreadyAutoBanned"
        do! botClient.DeleteMessageAsync(ChatId(message.Chat.Id), message.MessageId)
            |> safeTaskAwait (fun e -> logger.LogWarning ($"Failed to delete message {message.MessageId} from chat {message.Chat.Id}", e))

    let containsInvisibleMention =
        // Define all zero-width and whitespace-like characters to check
        let zeroWidthChars =
            [|
                '\u200B' // Zero Width Space
                '\u200C' // Zero Width Non-Joiner
                '\u200D' // Zero Width Joiner
                '\u2060' // Word Joiner
                '\u200E' // Left-to-Right Mark
                '\u200F' // Right-to-Left Mark
                '\uFEFF' // Zero Width No-Break Space
                '\u2800' // Braille Pattern Blank
            |]

        let checkEntity (text: string) (entity: MessageEntity) =
            match entity.Type with
            | Enums.MessageEntityType.TextMention | Enums.MessageEntityType.Mention ->
                let startIdx = entity.Offset
                let endIdx = entity.Offset + entity.Length - 1

                if startIdx < 0 || endIdx >= text.Length then
                    false
                else
                    let entityText = text[startIdx..endIdx]
                    entityText
                    |> Seq.exists (fun c ->
                        Char.IsControl c || Array.contains c zeroWidthChars
                    )
            | _ -> false

        match Option.ofObj message.Text, Option.ofObj message.Entities with
        | Some text, Some entities ->
            entities |> Array.exists (checkEntity text)
        | _ -> false

    if containsInvisibleMention then
        // delete message
        do! killSpammerAutomated botClient botConfig message logger true 0.0

    elif botConfig.MlEnabled && message.TextOrCaption <> null then
        use mlActivity = botActivity.StartActivity("mlPrediction")

        let shouldBeSkipped =
            // skip prediction for vahters or local admins
            if botConfig.AllowedUsers.ContainsValue message.From.Id
               || UpdateChatAdmins.Admins.Contains message.From.Id then
                true
            else

            match botConfig.MlStopWordsInChats.TryGetValue message.Chat.Id with
            | true, stopWords ->
                stopWords
                |> Seq.exists (fun sw -> message.TextOrCaption.Contains(sw, StringComparison.OrdinalIgnoreCase))
            | _ -> false
        %mlActivity.SetTag("skipPrediction", shouldBeSkipped)

        if not shouldBeSkipped then
            let! usrMsgCount = DB.countUniqueUserMsg message.From.Id

            match ml.Predict(message.TextOrCaption, usrMsgCount, message.Entities)  with
            | Some prediction ->
                %mlActivity.SetTag("spamScoreMl", prediction.Score)

                if prediction.Score >= botConfig.MlSpamThreshold then
                    // delete message
                    do! killSpammerAutomated botClient botConfig message logger botConfig.MlSpamDeletionEnabled prediction.Score

                    if botConfig.MlSpamAutobanEnabled then
                        // trigger auto-ban check
                        do! autoBan botUser botClient botConfig message logger
                elif prediction.Score >= botConfig.MlWarningThreshold then
                    // just warn
                    do! killSpammerAutomated botClient botConfig message logger false prediction.Score
                else
                    // not a spam
                    ()
            | None ->
                // no prediction (error or not ready yet)
                ()

    do!
        message
        |> DbMessage.newMessage
        |> DB.insertMessage
        |> taskIgnore
}

let adminCommand
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (vahter: DbUser)
    (message: Message) =
    
    // aux functions to overcome annoying FS3511: This state machine is not statically compilable.
    let banOnReplyAux() = task {
        let authed =
            isBanAuthorized
                botConfig
                message.ReplyToMessage
                vahter
                logger
        if authed then
            do! banOnReply botClient botConfig message vahter logger
    }
    let unbanAux() = task {
        if isUserVahter botConfig vahter then
            let targetUserId = message.Text.Split(" ", StringSplitOptions.RemoveEmptyEntries)[1] |> int64
            let! userToUnban = DB.getUserById targetUserId
            match userToUnban with
            | None ->
                logger.LogWarning $"User {vahter.username} ({vahter.id}) tried to unban non-existing user {targetUserId}"
            | Some userToUnban ->
                do! unban botClient botConfig message vahter userToUnban logger
    }
    let softBanOnReplyAux() = task {
        let authed =
            isBanAuthorized
                botConfig
                message.ReplyToMessage
                vahter
                logger
        if authed then
            do! softBanMsg botClient botConfig message vahter logger
    }

    task {
        use _ = botActivity.StartActivity("adminCommand")
        // delete command message
        let deleteCmdTask = task {
            use _ = 
                botActivity
                    .StartActivity("deleteCmdMsg")
                    .SetTag("msgId", message.MessageId)
                    .SetTag("chatId", message.Chat.Id)
                    .SetTag("chatUsername", message.Chat.Username)
            recordDeletedMessage message.Chat.Id message.Chat.Username "adminCommand"
            do! botClient.DeleteMessageAsync(ChatId(message.Chat.Id), message.MessageId)
                |> safeTaskAwait (fun e -> logger.LogWarning ($"Failed to delete ping message {message.MessageId} from chat {message.Chat.Id}", e))
        }
        // check that user is allowed to (un)ban others
        if isBanOnReplyCommand message then
            do! banOnReplyAux()
        elif isUnbanCommand message then
            do! unbanAux()
        elif isSoftBanOnReplyCommand message then
            do! softBanOnReplyAux()
        // ping command for testing that bot works and you can talk to it
        elif isPingCommand message then
            do! ping botClient message
        do! deleteCmdTask
    }

let onMessage
    (botUser: DbUser)
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (ml: MachineLearning)
    (message: Message) = task {
    use banOnReplyActivity = botActivity.StartActivity("onMessage")

    // early return if we can't process it
    if isNull message || isNull message.From then
        logger.LogWarning "Received update without message"
    else

    // early return if we don't monitor this chat
    if not (botConfig.ChatsToMonitor.ContainsValue message.Chat.Id) then
        ()
    else

    %banOnReplyActivity
        .SetTag("chatId", message.Chat.Id)
        .SetTag("chatUsername", message.Chat.Username)

    // metrics: count every processed message per chat
    messagesProcessedCounter.Add(1L, tagsForChat message.Chat.Id message.Chat.Username)

    // upserting user to DB
    let! user =
        DbUser.newUser message.From
        |> DB.upsertUser

    // check if message comes from channel, we should delete it immediately
    if botConfig.ShouldDeleteChannelMessages && isChannelMessage message then
        do! deleteChannelMessage botClient message logger

    // check if message is a known command from authorized user
    elif isKnownCommand message && isUserVahter botConfig user then
        do! adminCommand botClient botConfig logger user message

    // if message is not a command from authorized user, just save it ID to DB
    else
        do! justMessage botUser botClient botConfig logger ml message
}

let tryEnrichMessageWithOcr
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (computerVision: IComputerVision)
    (logger: ILogger)
    (update: Update) = task {
    if botConfig.OcrEnabled then
        let message = update.EditedOrMessage
        if not (isNull message.Photo) && message.Photo.Length > 0 then
            use activity = botActivity.StartActivity("ocrEnrichment")
            try
                let candidatePhotos =
                    message.Photo
                    |> Array.filter (fun p ->
                        let size = int64 p.FileSize
                        size = 0L || size <= botConfig.OcrMaxFileSizeBytes)

                if candidatePhotos.Length = 0 then
                    logger.LogWarning(
                        "No photos under OCR limit of {LimitBytes} bytes for message {MessageId}",
                        botConfig.OcrMaxFileSizeBytes,
                        message.MessageId)
                else
                    let largestPhoto =
                        candidatePhotos
                        |> Seq.filter (fun p -> p.FileSize.HasValue)
                        |> Seq.maxBy (fun p -> p.FileSize.Value)

                    %activity.SetTag("photoId", largestPhoto.FileId)

                    let! file = botClient.GetFileAsync(largestPhoto.FileId)

                    if String.IsNullOrWhiteSpace file.FilePath then
                        logger.LogWarning("Failed to resolve file path for photo {PhotoId}", largestPhoto.FileId)
                    else
                        let fileUrl = $"https://api.telegram.org/file/bot{botConfig.BotToken}/{file.FilePath}"
                        %activity.SetTag("fileUrl", fileUrl)
                        let! ocrText = computerVision.TextFromImageUrl fileUrl

                        if not (String.IsNullOrWhiteSpace ocrText) then
                            let baseText = message.TextOrCaption
                            let enrichedText =
                                if String.IsNullOrWhiteSpace baseText then
                                    ocrText
                                else
                                    $"{baseText}\n{ocrText}"
                            logger.LogDebug (
                                "Enriched message {MessageId} with OCR text {EnrichedText} of length {OcrTextLength}",
                                update.EditedOrMessage.MessageId,
                                enrichedText,
                                ocrText.Length
                            )
                            message.Text <- enrichedText
                            %activity.SetTag("ocrTextLength", enrichedText.Length)
            with ex ->
                logger.LogError(ex, "Failed to process OCR for message {MessageId}", update.EditedOrMessage.MessageId)
}

let vahterMarkedAsNotSpam
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (vahter: DbUser)
    (msg: MessageWrapper) = task {
    let msgId = msg.message.MessageId
    let chatId = msg.message.Chat.Id
    let chatName = msg.message.Chat.Username
    use _ =
        botActivity
            .StartActivity("vahterMarkedAsNotSpam")
            .SetTag("messageId", msgId)
            .SetTag("chatId", chatId)
    let dbMessage = DbMessage.newMessage msg.message
    do! DB.markMessageAsFalsePositive dbMessage
    do! DB.unbanUserByBot dbMessage

    let vahterUsername = vahter.username |> Option.defaultValue null
    
    let logMsg = $"Vahter {prependUsername vahterUsername} ({vahter.id}) marked message {msgId} in {prependUsername chatName}({chatId}) as false-positive (NOT A SPAM)\n{msg.message.TextOrCaption}"
    do! botClient.SendTextMessageAsync(
            chatId = ChatId(botConfig.ActionChannelId),
            text = logMsg,
            messageThreadId = botConfig.ActionAllLogsTopicId
        ) |> taskIgnore
    logger.LogInformation logMsg
}

let vahterMarkedAsSpam
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (vahter: DbUser)
    (message: MessageWrapper) = task {
    let msgId = message.message.MessageId
    let chatId = message.message.Chat.Id
    use _ =
        botActivity
            .StartActivity("vahterMarkedAsSpam")
            .SetTag("messageId", msgId)
            .SetTag("chatId", chatId)

    let isAuthed = isBanAuthorized botConfig message.message vahter logger
    if isAuthed then
        do! totalBan
                botClient
                botConfig
                message.message
                vahter
                logger
}

// just an aux function to reduce indentation in onCallback and prevent FS3511
let onCallbackAux
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (onCallbackActivity: Activity)
    (vahter: DbUser)
    (dbCallback: DbCallback)
    (callbackQuery: CallbackQuery)= task {
    let callback = dbCallback.data
    let msg = match callback with NotASpam m | Spam m -> m
    
    // Determine action type based on callback data and topic
    let actionType = 
        match callback with
        | Spam _ -> "potential_kill"
        | NotASpam _ -> 
            if dbCallback.action_topic_id = Some botConfig.ActionDetectedTopicId 
            then "detected_not_spam"
            else "potential_not_spam"
    
    // Level 2: Try to record action (protection between /ban and button click)
    let! actionRecorded = DB.tryRecordVahterAction 
                            vahter.id actionType msg.message.From.Id 
                            msg.message.Chat.Id msg.message.MessageId
    
    if actionRecorded then
        // We are first - execute the action
        %onCallbackActivity.SetTag("actionRecorded", true)
        match callback with
        | NotASpam msg ->
            %onCallbackActivity.SetTag("type", "NotASpam")
            do! vahterMarkedAsNotSpam botClient botConfig logger vahter msg
        | Spam msg ->
            %onCallbackActivity.SetTag("type", "Spam")
            do! vahterMarkedAsSpam botClient botConfig logger vahter msg
        
        do! botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Done! +1 🎯")
    else
        // Someone already handled via /ban
        %onCallbackActivity.SetTag("actionRecorded", false)
        logger.LogInformation $"Action already recorded for message {msg.message.MessageId} in chat {msg.message.Chat.Id}"
        do! botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Already handled by another vahter")
    
    // Always delete message from action channel (empty inbox)
    match dbCallback.action_message_id with
    | Some msgId ->
        do! botClient.DeleteMessageAsync(
                ChatId(botConfig.ActionChannelId),
                msgId
            ) |> safeTaskAwait (fun e -> logger.LogWarning ($"Failed to delete message {msgId} from action channel", e))
    | None -> ()
}

let onCallback
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (callbackQuery: CallbackQuery) = task {
    use onCallbackActivity = botActivity.StartActivity("onCallback")
    %onCallbackActivity.SetTag("callbackId", callbackQuery.Data)
    
    let callbackId = Guid.Parse callbackQuery.Data
    
    // Level 1: Atomically get and delete callback (protection between button clicks)
    match! DB.getCallbackAtomic callbackId with
    | None ->
        // Callback already processed by another vahter
        logger.LogInformation $"Callback {callbackId} already processed"
        do! botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Already processed")
    | Some dbCallback ->
        %onCallbackActivity.SetTag("callbackData", dbCallback.data)
        let callback = dbCallback.data
        
        match! DB.getUserById callbackQuery.From.Id with
        | None ->
            logger.LogWarning $"User {callbackQuery.From.Username} ({callbackQuery.From.Id}) tried to press callback button while not being in DB"
            do! botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "You are not in DB")
        | Some vahter ->
            %onCallbackActivity.SetTag("vahterUsername", vahter.username)
            %onCallbackActivity.SetTag("vahterId", vahter.id)
            
            // only vahters should be able to press message buttons
            let isAuthed = botConfig.AllowedUsers.ContainsValue vahter.id
            if not isAuthed then
                logger.LogWarning $"User {callbackQuery.From.Username} ({callbackQuery.From.Id}) tried to press callback button while not being a certified vahter"
                do! botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Not authorized")
            else
                do! onCallbackAux
                        botClient
                        botConfig
                        logger
                        onCallbackActivity
                        vahter
                        dbCallback
                        callbackQuery
}

let onMessageReaction (logger: ILogger) (reaction: MessageReactionUpdated) =
    use _ =
        botActivity
            .StartActivity("messageReaction")
            .SetTag("chatId", reaction.Chat.Id)
            .SetTag("chatUsername", reaction.Chat.Username)
            .SetTag("messageId", reaction.MessageId)
            .SetTag("userId", reaction.User.Id)
            .SetTag("userUsername", reaction.User.Username)
    logger.LogInformation(
        "Reaction from {Username} ({UserId}) on message {MessageId} in {ChatUsername} ({ChatId})",
        reaction.User.Username,
        reaction.User.Id,
        reaction.MessageId,
        reaction.Chat.Username,
        reaction.Chat.Id
    )

let onUpdate
    (botUser: DbUser)
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (ml: MachineLearning)
    (computerVision: IComputerVision)
    (update: Update) = task {
    use _ = botActivity.StartActivity("onUpdate")
    if update.CallbackQuery <> null then
        do! onCallback botClient botConfig logger update.CallbackQuery
    elif update.MessageReaction <> null then
        onMessageReaction logger update.MessageReaction
    elif update.EditedOrMessage <> null then
        do! tryEnrichMessageWithOcr botClient botConfig computerVision logger update
        do! onMessage botUser botClient botConfig logger ml update.EditedOrMessage
    else
        // unknown update type, just log and ignore
        logger.LogWarning("Unknown update type: {UpdateType}", update.Type)
}
