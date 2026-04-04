module VahterBanBot.Bot

open System
open System.Diagnostics
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open Telegram.Bot.Types.ReplyMarkups
open VahterBanBot.ML
open VahterBanBot.ComputerVision
open VahterBanBot.LlmTriage
open VahterBanBot.Telemetry
open VahterBanBot.Types

// Telegram.Bot.Types.User is shadowed by VahterBanBot.Types.User
type TgUser = Telegram.Bot.Types.User
open VahterBanBot.Utils
open VahterBanBot.UpdateChatAdmins
open VahterBanBot.Metrics

let isPingCommand (msg: TgMessage) =
    msg.Text = "/ban ping"

let isBanCommand (msg: TgMessage) =
    msg.Text = "/ban"
    
let isUnbanCommand (msg: TgMessage) =
    msg.Text.StartsWith "/unban "

let isSoftBanCommand (msg: TgMessage) =
    msg.Text.StartsWith "/sban"

let isSoftBanOnReplyCommand (msg: TgMessage) =
    isSoftBanCommand msg &&
    msg.ReplyToMessage.IsSome

let isBanOnReplyCommand (msg: TgMessage) =
    isBanCommand msg &&
    msg.ReplyToMessage.IsSome

let isMessageFromAllowedChats (botConfig: BotConfiguration) (msg: TgMessage) =
    botConfig.ChatsToMonitor.ContainsValue msg.ChatId
    
let isUserVahter (botConfig: BotConfiguration) (user: User) =
    botConfig.AllowedUsers.ContainsValue user.Id

let isBannedPersonAdmin (botConfig: BotConfiguration) (msg: TgMessage) =
    botConfig.AllowedUsers.ContainsValue msg.SenderId

let isKnownCommand (msg: TgMessage) =
    msg.Text <> null &&
    (isPingCommand msg ||
     isBanCommand msg ||
     isUnbanCommand msg ||
     isSoftBanCommand msg)

let isBanAuthorized
    (botConfig: BotConfiguration)
    (bannedMsg: TgMessage)
    (vahter: User)
    (logger: ILogger) =
    let fromUserId = vahter.Id
    let fromUsername = defaultArg vahter.Username null
    let chatId = bannedMsg.ChatId
    let chatUsername = bannedMsg.ChatUsername
    let targetUserId = bannedMsg.SenderId
    let targetUsername = bannedMsg.SenderUsername
    
    // check that user is allowed to ban others
    if isUserVahter botConfig vahter then
        if not(isMessageFromAllowedChats botConfig bannedMsg) then
            logger.LogWarning $"User {fromUsername} {fromUserId} tried to ban user {prependUsername targetUsername} ({targetUserId}) from not allowed chat {chatUsername} ({chatId})"
            false
        // check that user is not trying to ban other admins
        elif isBannedPersonAdmin botConfig bannedMsg then
            logger.LogWarning $"User {fromUsername} ({fromUserId}) tried to ban admin {prependUsername targetUsername} ({targetUserId}) in chat {chatUsername} ({chatId})"
            false
        else
            true
    else
        logger.LogWarning $"User {fromUsername} ({fromUserId}) tried to ban user {prependUsername targetUsername} ({targetUserId}) without being admin in chat {chatUsername} ({chatId})"
        false
    
let banInAllChats (botConfig: BotConfiguration) (botClient: ITelegramBotClient) targetUserId = task {
    let banTasks =
        botConfig.ChatsToMonitor
        |> Seq.map (fun (KeyValue(chatUserName, chatId)) -> task {
            // ban user in each chat
            try
                do! botClient.BanChatMember(ChatId chatId, targetUserId, DateTime.UtcNow.AddMonths 13)
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
        do! botClient.RestrictChatMember(chatId, targetUserId, permissions, untilDate = untilDate)
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
                do! botClient.UnbanChatMember(ChatId chatId, targetUserId, true)
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

[<RequireQualifiedAccess>]
type LoggedAction =
    | Ban of {| chat: Chat; actor: Actor; target: User; deletedUserMessages: int |}
    | Unban of {| chat: Chat; actor: Actor; target: User |}
    member this.Chat = 
        match this with
        | Ban x -> x.chat
        | Unban x -> x.chat
    member this.Actor =
        match this with
        | Ban x -> x.actor
        | Unban x -> x.actor
    member this.Target =
        match this with
        | Ban x -> x.target
        | Unban x -> x.target

let aggregateResultInLogMsg
    (loggedAction: LoggedAction)
    (logger: ILogger)
    (results: Result<string * int64, string * int64 * exn> []) =

    let resultType =
        match loggedAction with
        | LoggedAction.Ban _ -> "ban"
        | LoggedAction.Unban _ -> "unban"
    
    let maybeActorId, actorDisplayName =
        match loggedAction.Actor with
        | Actor.User u       -> Some u.userId, defaultArg u.username null |> prependUsername
        | Actor.Bot None     -> None, "Bot"
        | Actor.Bot (Some b) -> Some b.botUserId, b.botUsername |> prependUsername
        | Actor.ML           -> None, "ML"
        | Actor.LLM l        -> None, $"LLM/{l.modelName}"

    let sanitizedUsername = defaultArg loggedAction.Target.Username null |> prependUsername
    let targetUserId = loggedAction.Target.Id
    let actorIdStr =
        match maybeActorId with
        | Some id -> $"({id})"
        | None -> ""

    let chatName = loggedAction.Chat.Username
    let chatId = loggedAction.Chat.Id
    
    let logMsgBuilder = StringBuilder()
    %logMsgBuilder.Append($"Vahter {prependUsername actorDisplayName}{actorIdStr} {resultType}ned {sanitizedUsername} ({targetUserId}) in {prependUsername chatName}({chatId})")
    
    // we don't want to spam logs channel if all is good
    let allChatsOk = results |> Array.forall Result.isOk
    if allChatsOk then
        %logMsgBuilder.AppendLine " in all chats"
        match loggedAction with
        | LoggedAction.Ban banInfo ->
            %logMsgBuilder.AppendLine $"Deleted {banInfo.deletedUserMessages} messages"
        | _ -> ()
    else
        match loggedAction with
        | LoggedAction.Ban banInfo ->
            %logMsgBuilder.AppendLine ""
            %logMsgBuilder.AppendLine $"Deleted {banInfo.deletedUserMessages} messages in chats:"
        | _ -> ()

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

let softBanResultInLogMsg (msg: TgMessage) (vahter: User) (duration: int) =
    let logMsgBuilder = StringBuilder()
    let vahterUsername = defaultArg vahter.Username null
    let untilDate = (DateTime.UtcNow.AddHours duration).ToString "u"
    %logMsgBuilder.Append $"Vahter {prependUsername vahterUsername}({vahter.Id}) "
    %logMsgBuilder.Append $"softbanned {prependUsername msg.SenderUsername}({msg.SenderId}) "
    %logMsgBuilder.Append $"in {prependUsername msg.ChatUsername}({msg.ChatId}) "
    %logMsgBuilder.Append $"until {untilDate}"
    string logMsgBuilder


let ping
    (botClient: ITelegramBotClient)
    (msg: TgMessage) = task {
    use _ = botActivity.StartActivity("ping")
    do! botClient.SendMessage(ChatId(msg.ChatId), "pong") |> taskIgnore
}

let totalBan
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (msg: TgMessage)
    (actor: Actor)
    (logger: ILogger) = task {
    use banOnReplyActivity = botActivity.StartActivity("totalBan")
    %banOnReplyActivity
        .SetTag("actor", caseName actor)
        .SetTag("targetId", msg.SenderId)
        .SetTag("targetUsername", msg.SenderUsername)
    match actor with
    | Actor.User u ->
        %banOnReplyActivity.SetTag("vahterId", u.userId)
    | Actor.LLM l ->
        %banOnReplyActivity
            .SetTag("modelName", l.modelName)
            .SetTag("promptHash", l.promptHash)
    | _ -> ()
        
    // delete message
    let deleteMsgTask = task {
        use _ =
            botActivity
                .StartActivity("deleteMsg")
                .SetTag("msgId", msg.MessageId)
                .SetTag("chatId", msg.ChatId)
                .SetTag("chatUsername", msg.ChatUsername)
        recordDeletedMessage msg.ChatId msg.ChatUsername "totalBan_initial"
        do! botClient.DeleteMessage(ChatId(msg.ChatId), msg.MessageId)
            |> safeTaskAwait (fun e -> logger.LogWarning ($"Failed to delete message {msg.MessageId} from chat {msg.ChatId}", e))
    }

    // update user in DB
    let! updatedUser =
        msg.SenderUsername
        |> Option.ofObj
        |> DB.upsertUser msg.SenderId
        
    let deletedUserMessagesTask = task {
        let fromUserId = msg.SenderId
        let! allUserMessages = DB.getUserMessages fromUserId
        logger.LogInformation($"Deleting {allUserMessages.Length} messages from user {fromUserId}")
        
        // delete all recorded messages from user in all chats
        do!
            allUserMessages
            |> Seq.map (fun m -> task {
                try
                    use _ =
                        botActivity
                            .StartActivity("deleteMsg")
                            .SetTag("msgId", m.message_id)
                            .SetTag("chatId", m.chat_id)
                    recordDeletedMessage m.chat_id null "totalBan_history"
                    do! botClient.DeleteMessage(ChatId(m.chat_id), m.message_id)
                with e ->
                    logger.LogWarning ($"Failed to delete message {m.message_id} from chat {m.chat_id}", e)
            })
            |> Task.WhenAll
            |> taskIgnore

        return allUserMessages.Length
    }
    
    // Clean up ALL callbacks for this user (they may have multiple spam messages)
    // This ensures empty inbox when user is banned
    let cleanupCallbacksTask = task {
        let! userCallbacks = DB.getActiveCallbacksByUserId msg.SenderId
        logger.LogInformation($"Cleaning up {userCallbacks.Length} callbacks for banned user {msg.SenderId}")
        
        do!
            userCallbacks
            |> Seq.map (fun callback -> task {
                // Delete message from action channel
                match callback.action_message_id with
                | Some msgId ->
                    do! botClient.DeleteMessage(ChatId(callback.action_channel_id), msgId)
                        |> safeTaskAwait (fun e -> logger.LogWarning ($"Failed to delete callback message {msgId} from action channel", e))
                | None -> ()
                // Expire callback
                do! DB.expireCallback callback.id
            })
            |> Task.WhenAll
            |> taskIgnore
    }
    
    // try ban user in all monitored chats
    let! banResults = banInAllChats botConfig botClient msg.SenderId
    let! deletedUserMessages = deletedUserMessagesTask
    do! cleanupCallbacksTask

    // produce aggregated log message
    let logMsg =
        aggregateResultInLogMsg
            (LoggedAction.Ban {| chat = msg.Chat; actor = actor; target = updatedUser; deletedUserMessages = deletedUserMessages |})
            logger
            banResults

    // metrics: count banned user per vahter for successful bans
    bannedUsersCounter.Add(1L, tagsForVahter actor)

    // add ban record to DB
    // NOTE: This writes to user:{userId} stream — separate from moderation:{chatId}:{messageId}.
    // Cross-stream writes are not atomic (standard ES tradeoff). If the process crashes between
    // writing moderation and user events, one stream may be ahead. This is acceptable because
    // recordUserBanned is idempotent (skips if already banned) and can be retried.
    do! DB.recordUserBanned msg.SenderId actor msg.ChatId msg.MessageId (Option.ofObj msg.Text)

    // log both to logger and to All Logs channel
    do! botClient.SendMessage(
            chatId = ChatId(botConfig.AllLogsChannelId),
            text = logMsg
        ) |> taskIgnore
    logger.LogInformation logMsg

    do! deleteMsgTask
}

let banOnReply
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (msg: TgMessage)
    (vahter: User)
    (logger: ILogger) = task {
    use banOnReplyActivity = botActivity.StartActivity("banOnReply")
    let targetMsg = msg.ReplyToMessage.Value
    %banOnReplyActivity
        .SetTag("vahterId", msg.SenderId)
        .SetTag("vahterUsername", msg.SenderUsername)
        .SetTag("targetId", targetMsg.SenderId)
        .SetTag("targetUsername", targetMsg.SenderUsername)
    
    // Try to record action (race condition protection between /ban and KILL button)
    let! actionRecorded = DB.tryRecordVahterAction 
                            vahter.Id ManualBan targetMsg.SenderId 
                            targetMsg.ChatId targetMsg.MessageId
    
    if actionRecorded then
        %banOnReplyActivity.SetTag("actionRecorded", true)
        let actor = Actor.User {| userId = vahter.Id; username = vahter.Username |}
        do! totalBan
                botClient
                botConfig
                targetMsg
                actor
                logger
    else
        %banOnReplyActivity.SetTag("actionRecorded", false)
        logger.LogInformation $"User {targetMsg.SenderId} already banned by another vahter"
}

let softBanMsg
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (commandMessage: TgMessage)
    (vahter: User)
    (logger: ILogger) = task {
        let messageToRemove = commandMessage.ReplyToMessage.Value
        
        use banOnReplyActivity = botActivity.StartActivity("softBanOnReply")
        %banOnReplyActivity
            .SetTag("vahterId", vahter.Id)
            .SetTag("vahterUsername", defaultArg vahter.Username null)
            .SetTag("targetId", messageToRemove.SenderId)
            .SetTag("targetUsername", messageToRemove.SenderUsername)
        
        let deleteMsgTask = task {
            use _ =
                botActivity
                    .StartActivity("deleteMsg")
                    .SetTag("msgId", messageToRemove.MessageId)
                    .SetTag("chatId", messageToRemove.ChatId)
                    .SetTag("chatUsername", messageToRemove.ChatUsername)
            recordDeletedMessage messageToRemove.ChatId messageToRemove.ChatUsername "softBan"
            do! botClient.DeleteMessage(ChatId(messageToRemove.ChatId), messageToRemove.MessageId)
                |> safeTaskAwait (fun e -> logger.LogWarning ($"Failed to delete reply message {messageToRemove.MessageId} from chat {messageToRemove.ChatId}", e))
        }
        
        let maybeDurationString = commandMessage.Text.Split " " |> Seq.last
        // use last value as soft ban duration
        let duration =
            match Int32.TryParse maybeDurationString with
            | true, x -> x
            | _ -> 24 // 1 day should be enough

        let logText = softBanResultInLogMsg messageToRemove vahter duration
        
        do! softBanInChat botClient (ChatId messageToRemove.ChatId) messageToRemove.SenderId duration |> taskIgnore
        do! deleteMsgTask
        
        do! botClient.SendMessage(
                chatId = ChatId(botConfig.AllLogsChannelId),
                text = logText
            ) |> taskIgnore
        logger.LogInformation logText
}

let unban
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (msg: TgMessage)
    (vahter: User)
    (userToUnban: User)
    (logger: ILogger) = task {
    use banOnReplyActivity = botActivity.StartActivity("unban")
    let targetUserId = userToUnban.Id
    %banOnReplyActivity
        .SetTag("vahterId", vahter.Id)
        .SetTag("vahterUsername", defaultArg vahter.Username null)
        .SetTag("targetId", targetUserId)
        .SetTag("targetUsername", userToUnban.Username)
        
    // delete ban record from event stream
    let actor = Actor.User {| userId = vahter.Id; username = vahter.Username |}
    do! DB.recordUserUnbanned userToUnban.Id actor

    // try unban user in all monitored chats
    let! unbanResults = unbanInAllChats botConfig botClient targetUserId
    
    // produce aggregated log message
    let logMsg =
        aggregateResultInLogMsg
            (LoggedAction.Unban {| chat = msg.Chat; actor = actor; target = userToUnban |})
            logger
            unbanResults

    // log both to logger and to All Logs channel
    do! botClient.SendMessage(
            chatId = ChatId(botConfig.AllLogsChannelId),
            text = logMsg
        ) |> taskIgnore
    logger.LogInformation logMsg
}

/// Checks user's social score and triggers auto-ban if below threshold
/// Returns true if user was auto-banned, false otherwise
let checkAndAutoBan
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (msg: TgMessage)
    (actor: Actor)
    (logger: ILogger) = task {
    if not botConfig.MlSpamAutobanEnabled then
        return false
    else
        use banOnReplyActivity = botActivity.StartActivity("checkAndAutoBan")
        %banOnReplyActivity
            .SetTag("spammerId", msg.SenderId)
            .SetTag("spammerUsername", msg.SenderUsername)
            .SetTag("actor", caseName actor)

        let! userStats = DB.getUserStatsByLastNMessages botConfig.MlSpamAutobanCheckLastMsgCount msg.SenderId
        let socialScore = userStats.good - userStats.bad

        %banOnReplyActivity.SetTag("socialScore", socialScore)

        if double socialScore <= botConfig.MlSpamAutobanScoreThreshold then
            // ban user in all monitored chats
            do! totalBan botClient botConfig msg actor logger
            let logStr = $"Auto-banned user {prependUsername msg.SenderUsername} ({msg.SenderId}) due to the low social score {socialScore}"
            logger.LogInformation logStr
            do! botClient.SendMessage(
                    chatId = ChatId(botConfig.AllLogsChannelId),
                    text = logStr
                ) |> taskIgnore
            return true
        else
            return false
}

let private formatReasonStr (reason: AutoDeleteReason) =
    match reason with
    | MlSpam r -> $"score: {r.score}"
    | ReactionSpam r -> $"reactions: {r.reactionCount}"
    | InvisibleMention -> "invisible mention"

/// Deletes spam message, posts to detected spam channel with override button,
/// and checks karma for auto-ban.
let deleteSpam
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (msg: TgMessage)
    (actor: Actor)
    (logger: ILogger)
    (reason: AutoDeleteReason) = task {
    use activity = botActivity.StartActivity("deleteSpam")
    %activity
        .SetTag("spammerId", msg.SenderId)
        .SetTag("spammerUsername", msg.SenderUsername)
        .SetTag("actor", caseName actor)

    // 1. Delete message + record BotAutoDeleted
    recordDeletedMessage msg.ChatId msg.ChatUsername "spamDeletion"
    do! botClient.DeleteMessage(ChatId(msg.ChatId), msg.MessageId)
        |> safeTaskAwait (fun e -> logger.LogWarning ($"Failed to delete message {msg.MessageId} from chat {msg.ChatId}", e))
    do! DB.recordBotAutoDeleted msg.ChatId msg.MessageId msg.SenderId reason

    // 2. Post to detected spam channel with "NOT a spam" override button
    let logMsg = $"Deleted spam ({formatReasonStr reason}) in {prependUsername msg.ChatUsername} ({msg.ChatId}) from {prependUsername msg.SenderUsername} ({msg.SenderId}) with text:\n{msg.Text}"
    let callbackId = Guid.NewGuid()
    do! DB.recordCallbackCreated callbackId (CallbackMessage.NotASpam { message = msg.RawMessage }) msg.SenderId botConfig.DetectedSpamChannelId
    let markup = InlineKeyboardMarkup [
        InlineKeyboardButton.WithCallbackData("✅ NOT a spam", string callbackId)
    ]
    let! sent = botClient.SendMessage(
        chatId = ChatId(botConfig.DetectedSpamChannelId),
        text = logMsg,
        replyMarkup = markup
    )
    do! DB.recordCallbackMessagePosted callbackId sent.MessageId

    // 3. All logs channel (readonly, no buttons)
    do! botClient.SendMessage(
            chatId = ChatId(botConfig.AllLogsChannelId),
            text = logMsg
        ) |> taskIgnore
    logger.LogInformation logMsg

    // 4. Karma check + autoban
    let! _ = checkAndAutoBan botClient botConfig msg actor logger
    ()
}

/// Reports uncertain spam to potential spam channel with KILL/SPAM/NOT SPAM buttons for human triage.
/// Does NOT delete the message.
let reportPotentialSpam
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (msg: TgMessage)
    (logger: ILogger)
    (reason: AutoDeleteReason) = task {
    use activity = botActivity.StartActivity("reportPotentialSpam")
    %activity
        .SetTag("spammerId", msg.SenderId)
        .SetTag("spammerUsername", msg.SenderUsername)

    let logMsg = $"Detected spam ({formatReasonStr reason}) in {prependUsername msg.ChatUsername} ({msg.ChatId}) from {prependUsername msg.SenderUsername} ({msg.SenderId}) with text:\n{msg.Text}"

    // Create three callbacks for human triage
    let killId = Guid.NewGuid()
    let softSpamId = Guid.NewGuid()
    let notSpamId = Guid.NewGuid()
    do! DB.recordCallbackCreated killId (CallbackMessage.Spam { message = msg.RawMessage }) msg.SenderId botConfig.PotentialSpamChannelId
    do! DB.recordCallbackCreated softSpamId (CallbackMessage.MarkAsSpam { message = msg.RawMessage }) msg.SenderId botConfig.PotentialSpamChannelId
    do! DB.recordCallbackCreated notSpamId (CallbackMessage.NotASpam { message = msg.RawMessage }) msg.SenderId botConfig.PotentialSpamChannelId
    let markup = InlineKeyboardMarkup [|
        InlineKeyboardButton.WithCallbackData("🚫 KILL", string killId);
        InlineKeyboardButton.WithCallbackData("⚠️ SPAM", string softSpamId);
        InlineKeyboardButton.WithCallbackData("✅ NOT SPAM", string notSpamId)
    |]
    let! sent = botClient.SendMessage(
        chatId = ChatId(botConfig.PotentialSpamChannelId),
        text = logMsg,
        replyMarkup = markup
    )
    for callbackId in [killId; softSpamId; notSpamId] do
        do! DB.recordCallbackMessagePosted callbackId sent.MessageId

    // All logs channel (readonly, no buttons)
    do! botClient.SendMessage(
            chatId = ChatId(botConfig.AllLogsChannelId),
            text = logMsg
        ) |> taskIgnore
    logger.LogInformation logMsg
}



let totalBanByReaction
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (reaction: MessageReactionUpdated)
    (targetUser: User)
    (logger: ILogger) = task {
    use activity = botActivity.StartActivity("totalBanByReaction")
    %activity
        .SetTag("targetId", targetUser.Id)
        .SetTag("targetUsername", (defaultArg targetUser.Username null))
        .SetTag("reactionCount", targetUser.ReactionCount)
        
    let deletedUserMessagesTask = task {
        let! allUserMessages = DB.getUserMessages targetUser.Id
        logger.LogInformation($"Deleting {allUserMessages.Length} messages from reaction spammer {targetUser.Id}")
        
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
                    recordDeletedMessage msg.chat_id null "totalBanByReaction_history"
                    do! botClient.DeleteMessage(ChatId(msg.chat_id), msg.message_id)
                with e ->
                    logger.LogWarning ($"Failed to delete message {msg.message_id} from chat {msg.chat_id}", e)
            })
            |> Task.WhenAll
            |> taskIgnore

        return allUserMessages.Length
    }
    
    // Clean up ALL callbacks for this user
    let cleanupCallbacksTask = task {
        let! userCallbacks = DB.getActiveCallbacksByUserId targetUser.Id
        logger.LogInformation($"Cleaning up {userCallbacks.Length} callbacks for banned reaction spammer {targetUser.Id}")
        
        do!
            userCallbacks
            |> Seq.map (fun callback -> task {
                match callback.action_message_id with
                | Some msgId ->
                    do! botClient.DeleteMessage(ChatId(callback.action_channel_id), msgId)
                        |> safeTaskAwait (fun e -> logger.LogWarning ($"Failed to delete callback message {msgId} from action channel", e))
                | None -> ()
                do! DB.expireCallback callback.id
            })
            |> Task.WhenAll
            |> taskIgnore
    }
    
    // ban user in all monitored chats
    let! banResults = banInAllChats botConfig botClient targetUser.Id
    let! deletedUserMessages = deletedUserMessagesTask
    do! cleanupCallbacksTask
    
    // Record the auto-deletion and ban events (cross-stream, not atomic — see totalBan comment)
    do! DB.recordBotAutoDeleted reaction.Chat.Id reaction.MessageId targetUser.Id (ReactionSpam {| reactionCount = targetUser.ReactionCount |})
    // No messageText for reaction spam — the ban reason is in the BotAutoDeleted event
    let actor = Actor.Bot (Some {| botUserId = botConfig.BotUserId; botUsername = botConfig.BotUserName |})
    do! DB.recordUserBanned targetUser.Id actor reaction.Chat.Id reaction.MessageId None
    
    // metrics
    bannedUsersCounter.Add(1L, tagsForVahter actor)
    
    // produce log message
    let sanitizedUsername = defaultArg targetUser.Username null |> prependUsername
    let allChatsOk = banResults |> Array.forall Result.isOk
    let logMsgBuilder = StringBuilder()
    %logMsgBuilder.Append $"🤖 Auto-banned reaction spammer {sanitizedUsername} ({targetUser.Id})"
    %logMsgBuilder.AppendLine $" with {targetUser.ReactionCount} reactions and {deletedUserMessages} messages"
    if not allChatsOk then
        %logMsgBuilder.AppendLine "Ban results:"
        for result in banResults do
            match result with
            | Ok(chatName, _) -> %logMsgBuilder.AppendLine $"  ✅ {prependUsername chatName}"
            | Error(chatName, _, e) -> %logMsgBuilder.AppendLine $"  ❌ {prependUsername chatName}: {e.Message}"
    let logMsg = string logMsgBuilder
    
    // log both to logger and to All Logs channel
    do! botClient.SendMessage(
            chatId = ChatId(botConfig.AllLogsChannelId),
            text = logMsg
        ) |> taskIgnore
    logger.LogInformation logMsg
}

/// Pure decision function: runs ML prediction + optional LLM triage, returns verdict.
let getAutoVerdict
    (botConfig: BotConfiguration)
    (llmTriage: ILlmTriage)
    (msg: TgMessage)
    (ml: MachineLearning)
    (usrMsgCount: int) = task {
    match ml.Predict(msg.Text, usrMsgCount, msg.Entities) with
    | None -> return None
    | Some prediction ->
        do! DB.recordMlScoredMessage msg.ChatId msg.MessageId (float prediction.Score) (prediction.Score >= botConfig.MlSpamThreshold)

        if prediction.Score >= botConfig.MlSpamThreshold then
            return Some (AutoVerdict.Spam (float prediction.Score, Actor.ML))
        elif prediction.Score >= botConfig.MlWarningThreshold then
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 60.)
            let! llmVerdict = llmTriage.Classify(msg, int64 usrMsgCount, cts.Token)
            match llmVerdict with
            | LlmVerdict.Kill ->
                let actor = Actor.LLM {| modelName = llmTriage.ModelName; promptHash = llmTriage.PromptHash |}
                return Some (AutoVerdict.Spam (float prediction.Score, actor))
            | LlmVerdict.NotSpam ->
                return Some (AutoVerdict.NotSpam (Actor.LLM {| modelName = llmTriage.ModelName; promptHash = llmTriage.PromptHash |}))
            | LlmVerdict.Skip | LlmVerdict.Error ->
                return Some (AutoVerdict.Uncertain (float prediction.Score))
        else
            return Some (AutoVerdict.NotSpam Actor.ML)
}

let processMessage
    (botUser: User)
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (ml: MachineLearning)
    (llmTriage: ILlmTriage)
    (msg: TgMessage) = task {
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

        match Option.ofObj msg.OriginalText, Option.ofObj msg.Entities with
        | Some text, Some entities ->
            entities |> Array.exists (checkEntity text)
        | _ -> false

    if containsInvisibleMention then
        do! deleteSpam botClient botConfig msg (Actor.Bot None) logger InvisibleMention

    elif botConfig.MlEnabled && msg.Text <> null then
        use mlActivity = botActivity.StartActivity("mlPrediction")

        let shouldBeSkipped =
            // skip prediction for automatic forwards from linked channels
            if msg.IsAutomaticForward then
                true
            // skip prediction for channel senders whose channel is in ChatsToMonitor
            elif msg.IsChannelSender
                 && botConfig.ChatsToMonitor.ContainsValue msg.SenderId then
                true
            // skip prediction for vahters or local admins
            elif botConfig.AllowedUsers.ContainsValue msg.SenderId
                 || UpdateChatAdmins.Admins.Contains msg.SenderId then
                true
            else

            match botConfig.MlStopWordsInChats.TryGetValue msg.ChatId with
            | true, stopWords ->
                stopWords
                |> Seq.exists (fun sw -> msg.Text.Contains(sw, StringComparison.OrdinalIgnoreCase))
            | _ -> false
        %mlActivity.SetTag("skipPrediction", shouldBeSkipped)

        if not shouldBeSkipped then
            let! usrMsgCount = DB.countUniqueUserMsg msg.SenderId

            if usrMsgCount >= botConfig.MlOldUserMsgCount then
                let logMsg = $"User {prependUsername msg.SenderUsername} ({msg.SenderId}) has {usrMsgCount} msgs — ML god shows mercy today, skipping triage"
                do! botClient.SendMessage(ChatId(botConfig.AllLogsChannelId), text = logMsg) |> taskIgnore
                logger.LogInformation logMsg
            else

            let! autoVerdict = getAutoVerdict botConfig llmTriage msg ml usrMsgCount
            match autoVerdict with
            | Some (AutoVerdict.Spam (score, actor)) ->
                %mlActivity.SetTag("spamScoreMl", score)
                let reason = MlSpam {| score = score |}
                if botConfig.MlSpamDeletionEnabled then
                    do! deleteSpam botClient botConfig msg actor logger reason
                else
                    do! reportPotentialSpam botClient botConfig msg logger reason
            | Some (AutoVerdict.Uncertain score) ->
                %mlActivity.SetTag("spamScoreMl", score)
                do! reportPotentialSpam botClient botConfig msg logger (MlSpam {| score = score |})
            | Some (AutoVerdict.NotSpam _) | None ->
                ()
}

let justMessage
    (botUser: User)
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (ml: MachineLearning)
    (llmTriage: ILlmTriage)
    (msg: TgMessage) = task {

    use _ =
        botActivity
            .StartActivity("justMessage")
            .SetTag("fromUserId", msg.SenderId)
            .SetTag("fromUsername", msg.SenderUsername)

    // Record message first so it's available for ban operations (e.g. totalBan → getUserMessages)
    if msg.IsEdit then
        do! DB.editMessage msg
    else
        do! DB.insertMessage msg

    // check if user got auto-banned already
    // that could happen due to the race condition between spammers mass messages
    // and the bot's processing queue
    let! user = DB.getUserById msg.SenderId
    if user |> Option.exists _.IsBanned then
        // just delete message and move on
        let logMsg = $"Bot deleted message {msg.MessageId} from {prependUsername msg.SenderUsername}({msg.SenderId}) in {prependUsername msg.ChatUsername}({msg.ChatId}) because user was already banned"
        logger.LogInformation logMsg
        do! botClient.SendMessage(
                chatId = ChatId(botConfig.AllLogsChannelId),
                text = logMsg
            ) |> taskIgnore
        recordDeletedMessage msg.ChatId msg.ChatUsername "alreadyAutoBanned"
        do! botClient.DeleteMessage(ChatId(msg.ChatId), msg.MessageId)
            |> safeTaskAwait (fun e -> logger.LogWarning ($"Failed to delete message {msg.MessageId} from chat {msg.ChatId}", e))

    else do!
        processMessage
            botUser
            botClient
            botConfig
            logger
            ml
            llmTriage
            msg
}

let adminCommand
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (vahter: User)
    (msg: TgMessage) =
    
    // aux functions to overcome annoying FS3511: This state machine is not statically compilable.
    let banOnReplyAux() = task {
        let authed =
            isBanAuthorized
                botConfig
                msg.ReplyToMessage.Value
                vahter
                logger
        if authed then
            do! banOnReply botClient botConfig msg vahter logger
    }
    let unbanAux() = task {
        if isUserVahter botConfig vahter then
            let targetUserId = msg.Text.Split(" ", StringSplitOptions.RemoveEmptyEntries)[1] |> int64
            let! userToUnban = DB.getUserById targetUserId
            match userToUnban with
            | None ->
                logger.LogWarning $"User {vahter.Username} ({vahter.Id}) tried to unban non-existing user {targetUserId}"
            | Some userToUnban ->
                do! unban botClient botConfig msg vahter userToUnban logger
    }
    let softBanOnReplyAux() = task {
        let authed =
            isBanAuthorized
                botConfig
                msg.ReplyToMessage.Value
                vahter
                logger
        if authed then
            do! softBanMsg botClient botConfig msg vahter logger
    }

    task {
        use _ = botActivity.StartActivity("adminCommand")
        // delete command message
        let deleteCmdTask = task {
            use _ = 
                botActivity
                    .StartActivity("deleteCmdMsg")
                    .SetTag("msgId", msg.MessageId)
                    .SetTag("chatId", msg.ChatId)
                    .SetTag("chatUsername", msg.ChatUsername)
            recordDeletedMessage msg.ChatId msg.ChatUsername "adminCommand"
            do! botClient.DeleteMessage(ChatId(msg.ChatId), msg.MessageId)
                |> safeTaskAwait (fun e -> logger.LogWarning ($"Failed to delete command message {msg.MessageId} from chat {msg.ChatId}", e))
        }
        // check that user is allowed to (un)ban others
        if isBanOnReplyCommand msg then
            do! banOnReplyAux()
        elif isUnbanCommand msg then
            do! unbanAux()
        elif isSoftBanOnReplyCommand msg then
            do! softBanOnReplyAux()
        // ping command for testing that bot works and you can talk to it
        elif isPingCommand msg then
            do! ping botClient msg
        do! deleteCmdTask
    }

let onMessage
    (botUser: User)
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (ml: MachineLearning)
    (llmTriage: ILlmTriage)
    (msg: TgMessage) = task {
    use banOnReplyActivity = botActivity.StartActivity("onMessage")

    // early return if we can't process it
    if not msg.HasSender then
        logger.LogWarning "Received message without resolvable sender"
    else

    // early return if we don't monitor this chat
    if not (botConfig.ChatsToMonitor.ContainsValue msg.ChatId) then
        ()
    else

    %banOnReplyActivity
        .SetTag("chatId", msg.ChatId)
        .SetTag("chatUsername", msg.ChatUsername)

    // metrics: count every processed message per chat
    messagesProcessedCounter.Add(1L, tagsForChat msg.ChatId msg.ChatUsername)

    // upserting user to DB
    let! user =
        DB.upsertUser msg.SenderId (Option.ofObj msg.SenderUsername)

    // check if message is a known command from authorized user
    if isKnownCommand msg && isUserVahter botConfig user then
        do! adminCommand botClient botConfig logger user msg

    // if message is not a command from authorized user, just save it ID to DB
    else
        do! justMessage botUser botClient botConfig logger ml llmTriage msg
}

let private selectLargestPhoto (photos: PhotoSize array) =
    let withSize = photos |> Array.filter (fun p -> p.FileSize.HasValue)
    if withSize.Length > 0 then
        withSize |> Array.maxBy (fun p -> p.FileSize.Value)
    else
        photos |> Array.maxBy (fun p -> p.Width * p.Height)

let private ocrPhotos
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (computerVision: IComputerVision)
    (logger: ILogger)
    (photos: PhotoSize array)
    (messageId: int) = task {
    let candidatePhotos =
        photos
        |> Array.filter (fun p ->
            let size = int64 p.FileSize
            size = 0L || size <= botConfig.OcrMaxFileSizeBytes)

    if candidatePhotos.Length = 0 then
        logger.LogWarning(
            "No photos under OCR limit of {LimitBytes} bytes for message {MessageId}",
            botConfig.OcrMaxFileSizeBytes,
            messageId)
        return None
    else
        let largestPhoto = selectLargestPhoto candidatePhotos

        let! file = botClient.GetFile(largestPhoto.FileId)

        if String.IsNullOrWhiteSpace file.FilePath then
            logger.LogWarning("Failed to resolve file path for photo {PhotoId}", largestPhoto.FileId)
            return None
        else
            let fileUrl = $"https://api.telegram.org/file/bot{botConfig.BotToken}/{file.FilePath}"
            let! ocrText = computerVision.TextFromImageUrl fileUrl
            if String.IsNullOrWhiteSpace ocrText then
                return None
            else
                return Some ocrText
}

let tryEnrichWithForwardedContent
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (computerVision: IComputerVision)
    (logger: ILogger)
    (msg: TgMessage) = task {
    if botConfig.ForwardSpamDetectionEnabled && isMessageFromAllowedChats botConfig msg then
        use activity = botActivity.StartActivity("forwardedContentEnrichment")
        try
            let mutable forwardedText: string = null

            if not (isNull msg.Quote)
               && not (String.IsNullOrWhiteSpace msg.Quote.Text) then
                forwardedText <- msg.Quote.Text
                %activity.SetTag("quoteTextLength", msg.Quote.Text.Length)

            if botConfig.OcrEnabled
               && not (isNull msg.ExternalReply)
               && not (isNull msg.ExternalReply.Photo)
               && msg.ExternalReply.Photo.Length > 0 then
                let! ocrText = ocrPhotos botClient botConfig computerVision logger msg.ExternalReply.Photo msg.MessageId
                match ocrText with
                | Some text ->
                    forwardedText <-
                        if isNull forwardedText then text
                        else $"{forwardedText}\n{text}"
                    %activity.SetTag("externalReplyOcrLength", text.Length)
                | None -> ()

            if not (String.IsNullOrWhiteSpace forwardedText) then
                msg.PrependText(forwardedText)
                logger.LogDebug(
                    "Enriched message {MessageId} with forwarded content of length {ForwardedLength}",
                    msg.MessageId,
                    forwardedText.Length
                )
                %activity.SetTag("enrichedTextLength", msg.Text.Length)
        with ex ->
            logger.LogError(ex, "Failed to process forwarded content for message {MessageId}", msg.MessageId)
}

let tryEnrichWithOcr
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (computerVision: IComputerVision)
    (logger: ILogger)
    (msg: TgMessage) = task {
    if botConfig.OcrEnabled
       && not (isNull msg.Photos) && msg.Photos.Length > 0
       && isMessageFromAllowedChats botConfig msg then
        use activity = botActivity.StartActivity("ocrEnrichment")
        try
            let! ocrResult = ocrPhotos botClient botConfig computerVision logger msg.Photos msg.MessageId
            match ocrResult with
            | Some ocrText ->
                msg.AppendText(ocrText)
                logger.LogDebug (
                    "Enriched message {MessageId} with OCR text of length {OcrTextLength}",
                    msg.MessageId,
                    ocrText.Length
                )
                %activity.SetTag("ocrTextLength", ocrText.Length)
            | None -> ()
        with ex ->
            logger.LogError(ex, "Failed to process OCR for message {MessageId}", msg.MessageId)
}

let tryEnrichWithInlineKeyboardText
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (msg: TgMessage) = task {
    if botConfig.InlineKeyboardSpamDetectionEnabled
       && not (isNull msg.ReplyMarkup)
       && not (isNull msg.ReplyMarkup.InlineKeyboard)
       && isMessageFromAllowedChats botConfig msg then
        use activity = botActivity.StartActivity("inlineKeyboardEnrichment")
        try
            let sb = StringBuilder()
            for row in msg.ReplyMarkup.InlineKeyboard do
                for button in row do
                    if not (String.IsNullOrWhiteSpace button.Text) then
                        %sb.AppendLine(button.Text)
                    if not (isNull button.Url) && not (String.IsNullOrWhiteSpace button.Url) then
                        %sb.AppendLine(button.Url)

            let buttonText = sb.ToString().TrimEnd([|'\r'; '\n'|])
            if not (String.IsNullOrWhiteSpace buttonText) then
                msg.AppendText(buttonText)
                logger.LogDebug(
                    "Enriched message {MessageId} with inline keyboard text of length {ButtonTextLength}",
                    msg.MessageId,
                    buttonText.Length
                )
                %activity.SetTag("buttonTextLength", buttonText.Length)
                %activity.SetTag("enrichedTextLength", msg.Text.Length)
        with ex ->
            logger.LogError(ex, "Failed to process inline keyboard text for message {MessageId}", msg.MessageId)
}

let vahterMarkedAsNotSpam
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (vahter: User)
    (tgMsg: TgMessage) = task {
    let msgId = tgMsg.MessageId
    let chatId = tgMsg.ChatId
    let chatName = tgMsg.ChatUsername
    use _ =
        botActivity
            .StartActivity("vahterMarkedAsNotSpam")
            .SetTag("messageId", msgId)
            .SetTag("chatId", chatId)
    do! DB.recordMessageMarkedHam chatId msgId (if isNull tgMsg.Text then "" else tgMsg.Text) None

    let vahterUsername = vahter.Username |> Option.defaultValue null
    
    let logMsg = $"Vahter {prependUsername vahterUsername} ({vahter.Id}) marked message {msgId} in {prependUsername chatName}({chatId}) as false-positive (NOT A SPAM)\n{tgMsg.Text}"
    do! botClient.SendMessage(
            chatId = ChatId(botConfig.AllLogsChannelId),
            text = logMsg
        ) |> taskIgnore
    logger.LogInformation logMsg
}

let vahterMarkedAsSpam
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (vahter: User)
    (tgMsg: TgMessage) = task {
    let msgId = tgMsg.MessageId
    let chatId = tgMsg.ChatId
    use _ =
        botActivity
            .StartActivity("vahterMarkedAsSpam")
            .SetTag("messageId", msgId)
            .SetTag("chatId", chatId)

    let isAuthed = isBanAuthorized botConfig tgMsg vahter logger
    if isAuthed then
        let actor = Actor.User {| userId = vahter.Id; username = vahter.Username |}
        do! totalBan
                botClient
                botConfig
                tgMsg
                actor
                logger
}

/// Soft spam handler - deletes message and marks as spam for ML, but does NOT ban user
/// User may get auto-banned if karma threshold is reached
let vahterSoftSpam
    (botUser: User)
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (vahter: User)
    (tgMsg: TgMessage) = task {
    let msgId = tgMsg.MessageId
    let chatId = tgMsg.ChatId
    let chatName = tgMsg.ChatUsername
    use _ =
        botActivity
            .StartActivity("vahterSoftSpam")
            .SetTag("messageId", msgId)
            .SetTag("chatId", chatId)
    
    // 1. Delete the message from original chat
    recordDeletedMessage chatId chatName "softSpam"
    do! botClient.DeleteMessage(ChatId(chatId), msgId)
        |> safeTaskAwait (fun e -> logger.LogWarning($"Failed to delete message {msgId} from chat {chatId}", e))
    
    // 2. Mark as spam (for ML training + karma)
    do! DB.recordMessageMarkedSpam chatId msgId None
    
    // 3. Log the action
    let vahterUsername = vahter.Username |> Option.defaultValue null
    let logMsg = $"Vahter {prependUsername vahterUsername} ({vahter.Id}) marked message {msgId} in {prependUsername chatName}({chatId}) as SPAM (soft, no ban)\n{tgMsg.Text}"
    do! botClient.SendMessage(
            chatId = ChatId(botConfig.AllLogsChannelId),
            text = logMsg
        ) |> taskIgnore
    logger.LogInformation logMsg
    
    // 4. Check auto-ban using shared logic (karma system)
    let actor = Actor.User {| userId = vahter.Id; username = vahter.Username |}
    let! _ = checkAndAutoBan botClient botConfig tgMsg actor logger
    ()
}

// just an aux function to reduce indentation in onCallback and prevent FS3511
let onCallbackAux
    (botUser: User)
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (onCallbackActivity: Activity)
    (vahter: User)
    (callbackState: Callback)
    (callbackData: CallbackMessage)
    (callbackQuery: CallbackQuery)= task {
    let wrapper = match callbackData with NotASpam m | Spam m | MarkAsSpam m -> m
    let tgMsg = TgMessage.Create(wrapper.message)
    
    // Determine action type based on callback data and channel
    let actionType = 
        match callbackData with
        | Spam _ -> PotentialKill
        | MarkAsSpam _ -> PotentialSoftSpam
        | NotASpam _ -> 
            if callbackState.ActionChannelId = botConfig.DetectedSpamChannelId 
            then DetectedNotSpam
            else PotentialNotSpam
    
    // Level 2: Try to record action (protection between /ban and button click)
    let! actionRecorded = DB.tryRecordVahterAction 
                            vahter.Id actionType tgMsg.SenderId 
                            tgMsg.ChatId tgMsg.MessageId
    
    if actionRecorded then
        // We are first - execute the action
        %onCallbackActivity.SetTag("actionRecorded", true)
        match callbackData with
        | NotASpam _ ->
            %onCallbackActivity.SetTag("type", "NotASpam")
            do! vahterMarkedAsNotSpam botClient botConfig logger vahter tgMsg
        | Spam _ ->
            %onCallbackActivity.SetTag("type", "Spam")
            do! vahterMarkedAsSpam botClient botConfig logger vahter tgMsg
        | MarkAsSpam _ ->
            %onCallbackActivity.SetTag("type", "MarkAsSpam")
            do! vahterSoftSpam botUser botClient botConfig logger vahter tgMsg
        
        do! botClient.AnswerCallbackQuery(callbackQuery.Id, "Done! +1 🎯")
            |> safeTaskAwait (fun e -> logger.LogWarning($"Failed to answer callback query {callbackQuery.Id}", e))
    else
        // Someone already handled via /ban
        %onCallbackActivity.SetTag("actionRecorded", false)
        logger.LogInformation $"Action already recorded for message {tgMsg.MessageId} in chat {tgMsg.ChatId}"
        do! botClient.AnswerCallbackQuery(callbackQuery.Id, "Already handled by another vahter")
            |> safeTaskAwait (fun e -> logger.LogWarning($"Failed to answer callback query {callbackQuery.Id}", e))
    
    // Always delete message from action channel (empty inbox)
    // and cleanup related callbacks (for potential spam with two buttons)
    match callbackState.ActionMessageId with
    | Some msgId ->
        // Expire sibling callbacks with same message_id
        do! DB.expireCallbacksByMessageId msgId
        // Delete message from channel
        do! botClient.DeleteMessage(
                ChatId(callbackState.ActionChannelId),
                msgId
            ) |> safeTaskAwait (fun e -> logger.LogWarning ($"Failed to delete message {msgId} from action channel", e))
    | None -> ()
}

let onCallback
    (botUser: User)
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (callbackQuery: CallbackQuery) = task {
    use onCallbackActivity = botActivity.StartActivity("onCallback")
    %onCallbackActivity.SetTag("callbackId", callbackQuery.Data)
    
    let callbackId = Guid.Parse callbackQuery.Data
    
    // Level 1: Atomically resolve callback (protection between button clicks)
    match! DB.resolveCallback callbackId with
    | None ->
        // Callback already processed by another vahter
        logger.LogInformation $"Callback {callbackId} already processed"
        do! botClient.AnswerCallbackQuery(callbackQuery.Id, "Already processed")
            |> safeTaskAwait (fun e -> logger.LogWarning($"Failed to answer callback query {callbackQuery.Id}", e))
    | Some callbackState ->
        let callbackData = deserializeCallbackData callbackState.Data.Value
        %onCallbackActivity.SetTag("callbackData", callbackData)
        
        match! DB.getUserById callbackQuery.From.Id with
        | None ->
            logger.LogWarning $"User {callbackQuery.From.Username} ({callbackQuery.From.Id}) tried to press callback button while not being in DB"
            do! botClient.AnswerCallbackQuery(callbackQuery.Id, "You are not in DB")
                |> safeTaskAwait (fun e -> logger.LogWarning($"Failed to answer callback query {callbackQuery.Id}", e))
        | Some vahter ->
            %onCallbackActivity.SetTag("vahterUsername", vahter.Username)
            %onCallbackActivity.SetTag("vahterId", vahter.Id)
            
            // only vahters should be able to press message buttons
            let isAuthed = botConfig.AllowedUsers.ContainsValue vahter.Id
            if not isAuthed then
                logger.LogWarning $"User {callbackQuery.From.Username} ({callbackQuery.From.Id}) tried to press callback button while not being a certified vahter"
                do! botClient.AnswerCallbackQuery(callbackQuery.Id, "Not authorized")
                    |> safeTaskAwait (fun e -> logger.LogWarning($"Failed to answer callback query {callbackQuery.Id}", e))
            else
                do! onCallbackAux
                        botUser
                        botClient
                        botConfig
                        logger
                        onCallbackActivity
                        vahter
                        callbackState
                        callbackData
                        callbackQuery
}

let onMessageReaction
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (reaction: MessageReactionUpdated) = task {
    use activity =
        botActivity
            .StartActivity("messageReaction")
            .SetTag("chatId", reaction.Chat.Id)
            .SetTag("chatUsername", reaction.Chat.Username)
            .SetTag("messageId", reaction.MessageId)
            .SetTag("userId", reaction.User.Id)
            .SetTag("userUsername", reaction.User.Username)

    // Check if reaction spam detection is enabled
    if not botConfig.ReactionSpamEnabled then
        %activity.SetTag("skipped", "reactionSpamDisabled")
    // Check if chat is monitored
    elif not (botConfig.ChatsToMonitor.ContainsValue reaction.Chat.Id) then
        %activity.SetTag("skipped", "chatNotMonitored")
    else
        // Calculate added reactions (new - old)
        let oldCount = if isNull reaction.OldReaction then 0 else reaction.OldReaction.Length
        let newCount = if isNull reaction.NewReaction then 0 else reaction.NewReaction.Length
        let added = newCount - oldCount
        
        %activity.SetTag("oldReactionCount", oldCount)
        %activity.SetTag("newReactionCount", newCount)
        %activity.SetTag("addedReactions", added)
        
        // Only process if reactions were added (not removed)
        if added > 0 then
            // Upsert user and increment reaction count atomically
            let! updatedUser =
                DB.upsertUserAndIncrementReactions reaction.User.Id (Option.ofObj reaction.User.Username) added
            
            %activity.SetTag("totalReactionCount", updatedUser.ReactionCount)
            
            // Check heuristics: if user has few messages but many reactions -> ban
            let! msgCount = DB.countUniqueUserMsg updatedUser.Id
            %activity.SetTag("messageCount", msgCount)
            
            if msgCount < botConfig.ReactionSpamMinMessages && 
               updatedUser.ReactionCount >= botConfig.ReactionSpamMaxReactions then
                logger.LogWarning(
                    "Reaction spam detected: {Username} ({UserId}) has {MsgCount} messages but {ReactionCount} reactions",
                    reaction.User.Username,
                    reaction.User.Id,
                    msgCount,
                    updatedUser.ReactionCount
                )
                %activity.SetTag("action", "ban")
                do! totalBanByReaction botClient botConfig reaction updatedUser logger
            else
                %activity.SetTag("action", "none")
}

let onUpdate
    (botUser: User)
    (botClient: ITelegramBotClient)
    (botConfig: BotConfiguration)
    (logger: ILogger)
    (ml: MachineLearning)
    (computerVision: IComputerVision)
    (llmTriage: ILlmTriage)
    (update: Update) = task {
    use _ = botActivity.StartActivity("onUpdate")
    if update.CallbackQuery <> null then
        do! onCallback botUser botClient botConfig logger update.CallbackQuery
    elif update.MessageReaction <> null then
        do! onMessageReaction botClient botConfig logger update.MessageReaction
    elif update.EditedOrMessage <> null then
        let isEdit = update.EditedMessage <> null
        let msg = TgMessage.Create(update.EditedOrMessage, isEdit = isEdit)
        do! tryEnrichWithForwardedContent botClient botConfig computerVision logger msg
        do! tryEnrichWithOcr botClient botConfig computerVision logger msg
        do! tryEnrichWithInlineKeyboardText botConfig logger msg
        do! onMessage botUser botClient botConfig logger ml llmTriage msg
    elif update.ChatMember <> null || update.MyChatMember <> null then
        // expected update type, nothing to do
        ()
    else
        // unknown update type, just log and ignore
        logger.LogWarning("Unknown update type: {UpdateType}", update.Type)
}
