﻿module VahterBanBot.Bot

open System
open System.Diagnostics
open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open Telegram.Bot.Types.ReplyMarkups
open VahterBanBot.ML
open VahterBanBot.Types
open VahterBanBot.Utils
open VahterBanBot.UpdateChatAdmins

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
        do! botClient.RestrictChatMemberAsync(chatId, targetUserId, permissions, Nullable(), untilDate)
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
        do! botClient.DeleteMessageAsync(ChatId(message.Chat.Id), message.MessageId)
            |> safeTaskAwait (fun e -> logger.LogError ($"Failed to delete message {message.MessageId} from chat {message.Chat.Id}", e))
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
                    do! botClient.DeleteMessageAsync(ChatId(msg.chat_id), msg.message_id)
                with e ->
                    logger.LogError ($"Failed to delete message {msg.message_id} from chat {msg.chat_id}", e)
            })
            |> Task.WhenAll
            |> taskIgnore

        return allUserMessages.Length
    }
    
    // try ban user in all monitored chats
    let! banResults = banInAllChats botConfig botClient message.From.Id
    let! deletedUserMessages = deletedUserMessagesTask
    
    // produce aggregated log message
    let logMsg = aggregateBanResultInLogMsg message.Chat vahter updatedUser logger deletedUserMessages banResults
    
    // add ban record to DB
    do! message
        |> DbBanned.banMessage vahter.id
        |> DB.banUser

    // log both to logger and to logs channel
    do! botClient.SendTextMessageAsync(ChatId(botConfig.LogsChannelId), logMsg) |> taskIgnore
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
    
    do! totalBan
            botClient
            botConfig
            message.ReplyToMessage
            vahter
            logger
}

let softBanMsg
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (message: Message)
    (vahter: DbUser)
    (logger: ILogger) = task {
        use banOnReplyActivity = botActivity.StartActivity("softBanOnReply")
        %banOnReplyActivity
            .SetTag("vahterId", vahter.id)
            .SetTag("vahterUsername", defaultArg vahter.username null)
            .SetTag("targetId", message.From.Id)
            .SetTag("targetUsername", message.From.Username)
        
        let deleteMsgTask = task {
            use _ =
                botActivity
                    .StartActivity("deleteMsg")
                    .SetTag("msgId", message.MessageId)
                    .SetTag("chatId", message.Chat.Id)
                    .SetTag("chatUsername", message.Chat.Username)
            do! botClient.DeleteMessageAsync(ChatId(message.Chat.Id), message.MessageId)
                |> safeTaskAwait (fun e -> logger.LogError ($"Failed to delete reply message {message.MessageId} from chat {message.Chat.Id}", e))
        }
        
        let maybeDurationString = message.Text.Split " " |> Seq.last
        // use last value as soft ban duration
        let duration =
            match Int32.TryParse maybeDurationString with
            | true, x -> x
            | _ -> 24 // 1 day should be enough

        let logText = softBanResultInLogMsg message vahter duration
        
        do! softBanInChat botClient (ChatId message.Chat.Id) message.From.Id duration |> taskIgnore
        do! deleteMsgTask
        
        do! botClient.SendTextMessageAsync(ChatId(botConfig.LogsChannelId), logText) |> taskIgnore
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

    // log both to logger and to logs channel
    do! botClient.SendTextMessageAsync(ChatId(botConfig.LogsChannelId), logMsg) |> taskIgnore
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
        do! botClient.DeleteMessageAsync(ChatId(message.Chat.Id), message.MessageId)
            |> safeTaskAwait (fun e -> logger.LogError ($"Failed to delete message {message.MessageId} from chat {message.Chat.Id}", e))
        // 0 here is the bot itself
        do! DbBanned.banMessage 0 message
            |> DB.banUserByBot

    let msgType = if deleteMessage then "Deleted" else "Detected"
    let logMsg = $"{msgType} spam (score: {score}) in {prependUsername message.Chat.Username} ({message.Chat.Id}) from {prependUsername message.From.Username} ({message.From.Id}) with text:\n{message.Text}"

    let! replyMarkup = task {
        if deleteMessage then
            let data = CallbackMessage.NotASpam { message = message }
            let! callback = DB.newCallback data
            return InlineKeyboardMarkup [
                InlineKeyboardButton.WithCallbackData("NOT a spam", string callback.id)
            ]
        else
            let spamData = CallbackMessage.Spam { message = message }
            let notSpamData = CallbackMessage.NotASpam { message = message }
            let! spamCallback = DB.newCallback spamData
            let! notSpamCallback = DB.newCallback notSpamData
            return InlineKeyboardMarkup [
                InlineKeyboardButton.WithCallbackData("KILL", string spamCallback.id)
                InlineKeyboardButton.WithCallbackData("NOT a spam", string notSpamCallback.id)
            ]
    }

    // log both to logger and to logs channel
    do! botClient.SendTextMessageAsync(ChatId(botConfig.LogsChannelId), logMsg, replyMarkup = replyMarkup) |> taskIgnore
    logger.LogInformation logMsg
}

let justMessage
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

    if botConfig.MlEnabled && message.Text <> null then
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
                |> Seq.exists (fun sw -> message.Text.Contains(sw, StringComparison.OrdinalIgnoreCase))
            | _ -> false
        %mlActivity.SetTag("skipPrediction", shouldBeSkipped)
        
        if not shouldBeSkipped then
            let! usrMsgCount = DB.countUniqueUserMsg message.From.Id
            
            match ml.Predict(message.Text, usrMsgCount)  with
            | Some prediction ->
                %mlActivity.SetTag("spamScoreMl", prediction.Score)
                
                if prediction.Score >= botConfig.MlSpamThreshold then
                    // delete message
                    do! killSpammerAutomated botClient botConfig message logger botConfig.MlSpamDeletionEnabled prediction.Score
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
            do! softBanMsg botClient botConfig message.ReplyToMessage vahter logger
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
            do! botClient.DeleteMessageAsync(ChatId(message.Chat.Id), message.MessageId)
                |> safeTaskAwait (fun e -> logger.LogError ($"Failed to delete ping message {message.MessageId} from chat {message.Chat.Id}", e))
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
        do! justMessage botClient botConfig logger ml message
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
    
    let logMsg = $"Vahter {prependUsername vahterUsername} ({vahter.id}) marked message {msgId} in {prependUsername chatName}({chatId}) as false-positive (NOT A SPAM)\n{msg.message.Text}"
    do! botClient.SendTextMessageAsync(ChatId(botConfig.LogsChannelId), logMsg) |> taskIgnore
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

let onCallback
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (callbackQuery: CallbackQuery) = task {
    use onCallbackActivity = botActivity.StartActivity("onCallback")
    %onCallbackActivity.SetTag("callbackId", callbackQuery.Data)
    
    let callbackId = Guid.Parse callbackQuery.Data
    
    match! DB.getCallback callbackId with
    | None ->
        logger.LogWarning $"Callback {callbackId} not found in DB"
    | Some dbCallback ->
        %onCallbackActivity.SetTag("callbackData", dbCallback.data)
        let callback = dbCallback.data
        match! DB.getUserById callbackQuery.From.Id with
        | None ->
            logger.LogWarning $"User {callbackQuery.From.Username} ({callbackQuery.From.Id}) tried to press callback button while not being in DB"
        | Some vahter ->
            %onCallbackActivity.SetTag("vahterUsername", vahter.username)
            %onCallbackActivity.SetTag("vahterId", vahter.id)
            
            // only vahters should be able to press message buttons
            let isAuthed = botConfig.AllowedUsers.ContainsValue vahter.id
            if not isAuthed then
                logger.LogWarning $"User {callbackQuery.From.Username} ({callbackQuery.From.Id}) tried to press callback button while not being a certified vahter"
            else
                match callback with
                | NotASpam msg ->
                    %onCallbackActivity.SetTag("type", "NotASpam")
                    do! vahterMarkedAsNotSpam botClient botConfig logger vahter msg
                | Spam msg ->
                    %onCallbackActivity.SetTag("type", "Spam")
                    do! vahterMarkedAsSpam botClient botConfig logger vahter msg
        do! DB.deleteCallback callbackId
    do! botClient.AnswerCallbackQueryAsync(callbackQuery.Id)
}

let onUpdate
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (ml: MachineLearning)
    (update: Update) = task {
    use _ = botActivity.StartActivity("onUpdate")
    if update.CallbackQuery <> null then
        do! onCallback botClient botConfig logger update.CallbackQuery
    else
        do! onMessage botClient botConfig logger ml update.Message
}
