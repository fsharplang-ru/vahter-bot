// VahterBanBot — Telegram moderation bot
namespace VahterBanBot

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Telegram.Bot
open Telegram.Bot.Types
open Telegram.Bot.Types.Enums
open Telegram.Bot.Types.ReplyMarkups
open VahterBanBot.ML
open VahterBanBot.ComputerVision
open VahterBanBot.LlmTriage
open VahterBanBot.ProfileFetcher
open VahterBanBot.Telemetry
open VahterBanBot.Types
open BotInfra.TelegramExtensions
open VahterBanBot.Utils
open BotInfra
open VahterBanBot.UpdateChatAdmins
open VahterBanBot.Metrics

// Telegram.Bot.Types.User is shadowed by VahterBanBot.Types.User
type TgUser = Telegram.Bot.Types.User

// ---------------------------------------------------------------------------
// Pure helper functions — no service dependencies, kept at module level
// ---------------------------------------------------------------------------

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

[<AutoOpen>]
module private BotHelpers =
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

    let isMessageFromAllowedChats (cfg: BotConfiguration) (msg: TgMessage) =
        cfg.ChatsToMonitor.ContainsValue msg.ChatId

    let isUserVahter (cfg: BotConfiguration) (user: User) =
        cfg.AllowedUsers.ContainsValue user.Id

    let isBannedPersonAdmin (cfg: BotConfiguration) (msg: TgMessage) =
        cfg.AllowedUsers.ContainsValue msg.SenderId

    let isKnownCommand (msg: TgMessage) =
        msg.Text <> null &&
        (isPingCommand msg ||
         isBanCommand msg ||
         isUnbanCommand msg ||
         isSoftBanCommand msg)

    let isBanAuthorized
        (cfg: BotConfiguration)
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
        if isUserVahter cfg vahter then
            if not(isMessageFromAllowedChats cfg bannedMsg) then
                logger.LogWarning $"User {fromUsername} {fromUserId} tried to ban user {prependUsername targetUsername} ({targetUserId}) from not allowed chat {chatUsername} ({chatId})"
                false
            // check that user is not trying to ban other admins
            elif isBannedPersonAdmin cfg bannedMsg then
                logger.LogWarning $"User {fromUsername} ({fromUserId}) tried to ban admin {prependUsername targetUsername} ({targetUserId}) in chat {chatUsername} ({chatId})"
                false
            else
                true
        else
            logger.LogWarning $"User {fromUsername} ({fromUserId}) tried to ban user {prependUsername targetUsername} ({targetUserId}) without being admin in chat {chatUsername} ({chatId})"
            false

    let safeTaskAwait onError (task: Task) =
        task.ContinueWith(fun (t: Task) ->
            if t.IsFaulted then
                onError t.Exception
        )

    let aggregateResultInLogMsg
        (loggedAction: LoggedAction)
        (logger: ILogger)
        (results: Result<string * int64, string * int64 * exn> []) =

        let resultType =
            match loggedAction with
            | LoggedAction.Ban _ -> "ban"
            | LoggedAction.Unban _ -> "unban"

        let maybeActorId =
            match loggedAction.Actor with
            | Actor.User u       -> Some u.userId
            | Actor.Bot (Some b) -> Some b.botUserId
            | Actor.Bot None | Actor.ML | Actor.LLM _ -> None
        let actorDisplayName = loggedAction.Actor.DisplayName

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

    let softBanResultInLogMsg (msg: TgMessage) (vahter: User) (duration: int) (now: DateTime) =
        let logMsgBuilder = StringBuilder()
        let vahterUsername = defaultArg vahter.Username null
        let untilDate = (now.AddHours duration).ToString "u"
        %logMsgBuilder.Append $"Vahter {prependUsername vahterUsername}({vahter.Id}) "
        %logMsgBuilder.Append $"softbanned {prependUsername msg.SenderUsername}({msg.SenderId}) "
        %logMsgBuilder.Append $"in {prependUsername msg.ChatUsername}({msg.ChatId}) "
        %logMsgBuilder.Append $"until {untilDate}"
        string logMsgBuilder

    let formatReasonStr (reason: AutoDeleteReason) (actor: Actor option) =
        let prefix = actor |> Option.map (fun a -> $"{a.DisplayName}, ") |> Option.defaultValue ""
        match reason with
        | AutoDeleteReason.MlSpam r           -> $"{prefix}score: {r.score}"
        | AutoDeleteReason.ReactionSpam r     -> $"{prefix}reactions: {r.reactionCount}"
        | AutoDeleteReason.InvisibleMention   -> $"{prefix}invisible mention"

    let selectLargestPhoto (photos: PhotoSize array) =
        let withSize = photos |> Array.filter (fun p -> p.FileSize.HasValue)
        if withSize.Length > 0 then
            withSize |> Array.maxBy (fun p -> p.FileSize.Value)
        else
            photos |> Array.maxBy (fun p -> p.Width * p.Height)


type BotService(
    botClient: ITelegramBotClient,
    botConfig: IOptions<BotConfiguration>,
    db: DbService,
    ml: MachineLearning,
    computerVision: IComputerVision,
    ocrCache: VahterBanBot.OcrCache.IOcrCache,
    llmTriage: ILlmTriage,
    reactionTriage: IReactionTriageClassifier,
    profileFetcher: IUserProfileFetcher,
    logger: ILogger<BotService>,
    timeProvider: TimeProvider
) =
    let utcNow () = timeProvider.GetUtcNow().UtcDateTime

    // -----------------------------------------------------------------------
    // Private members — Telegram chat operations
    // -----------------------------------------------------------------------

    member private _.BanInAllChats(targetUserId) = task {
        let banTasks =
            botConfig.Value.ChatsToMonitor
            |> Seq.map (fun (KeyValue(chatUserName, chatId)) -> task {
                try
                    do! botClient.BanChatMember(ChatId chatId, targetUserId, utcNow().AddMonths 13)
                    return Ok(chatUserName, chatId)
                with e ->
                    return Error (chatUserName, chatId, e)
            })
        return! Task.WhenAll banTasks
    }

    member private _.SoftBanInChat(chatId: ChatId, targetUserId, duration: int) = task {
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
        let untilDate = utcNow().AddHours duration
        try
            do! botClient.RestrictChatMember(chatId, targetUserId, permissions, untilDate = untilDate)
            return Ok(chatId, targetUserId)
        with e ->
            return Error(chatId, targetUserId, e)
    }

    member private _.UnbanInAllChats(targetUserId) = task {
        let unbanTasks =
            botConfig.Value.ChatsToMonitor
            |> Seq.map (fun (KeyValue(chatUserName, chatId)) -> task {
                try
                    do! botClient.UnbanChatMember(ChatId chatId, targetUserId, true)
                    return Ok(chatUserName, chatId)
                with e ->
                    return Error (chatUserName, chatId, e)
            })
        return! Task.WhenAll unbanTasks
    }

    // -----------------------------------------------------------------------
    // Private members — Core bot actions
    // -----------------------------------------------------------------------

    member private _.Ping(msg: TgMessage) = task {
        use _ = botActivity.StartActivity("ping")
        do! botClient.SendMessage(ChatId(msg.ChatId), "pong") |> taskIgnore
    }

    member private this.TotalBan(msg: TgMessage, actor: Actor) = task {
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
            db.UpsertUser(msg.SenderId, Option.ofObj msg.SenderUsername)

        let deletedUserMessagesTask = task {
            let fromUserId = msg.SenderId
            let! allUserMessages = db.GetUserMessages(fromUserId)
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

        // Clean up callbacks for this user only on manual bans — gives the vahter an empty inbox.
        // For auto-bans (ML/LLM/Bot) we leave the cards in Detected Spam as an audit trail; they
        // age out via DetectedSpamCleanupAge in Cleanup.runCleanup and don't race with concurrent
        // DeleteSpam posts from parallel webhooks for the same spammer.
        let cleanupCallbacksTask = task {
            match actor with
            | Actor.User _ ->
                let! userCallbacks = db.GetActiveCallbacksByUserId(msg.SenderId)
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
                        do! db.ExpireCallback(callback.id)
                    })
                    |> Task.WhenAll
                    |> taskIgnore
            | Actor.Bot _ | Actor.ML | Actor.LLM _ ->
                ()
        }

        // try ban user in all monitored chats
        let! banResults = this.BanInAllChats(msg.SenderId)
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
        do! db.RecordUserBanned(actor, msg, botConfig.Value.BanExpiryDays)

        // log both to logger and to All Logs channel
        do! botClient.SendMessage(
                chatId = ChatId(botConfig.Value.AllLogsChannelId),
                text = logMsg
            ) |> taskIgnore
        logger.LogInformation logMsg

        do! deleteMsgTask
    }

    member private this.BanOnReply(msg: TgMessage, vahter: User) = task {
        use banOnReplyActivity = botActivity.StartActivity("banOnReply")
        let targetMsg = msg.ReplyToMessage.Value
        %banOnReplyActivity
            .SetTag("vahterId", msg.SenderId)
            .SetTag("vahterUsername", msg.SenderUsername)
            .SetTag("targetId", targetMsg.SenderId)
            .SetTag("targetUsername", targetMsg.SenderUsername)

        // Try to record action (race condition protection between /ban and KILL button)
        let! actionRecorded = db.TryRecordVahterAction(
                                vahter.Id, ManualBan, targetMsg.SenderId,
                                targetMsg.ChatId, targetMsg.MessageId)

        if actionRecorded then
            %banOnReplyActivity.SetTag("actionRecorded", true)
            let actor = Actor.User {| userId = vahter.Id; username = vahter.Username |}
            do! this.TotalBan(targetMsg, actor)
        else
            %banOnReplyActivity.SetTag("actionRecorded", false)
            logger.LogInformation $"User {targetMsg.SenderId} already banned by another vahter"
    }

    member private this.SoftBanMsg(commandMessage: TgMessage, vahter: User) = task {
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

        let logText = softBanResultInLogMsg messageToRemove vahter duration (utcNow())

        do! this.SoftBanInChat(ChatId messageToRemove.ChatId, messageToRemove.SenderId, duration) |> taskIgnore
        do! deleteMsgTask

        do! botClient.SendMessage(
                chatId = ChatId(botConfig.Value.AllLogsChannelId),
                text = logText
            ) |> taskIgnore
        logger.LogInformation logText
    }

    member private this.Unban(msg: TgMessage, vahter: User, userToUnban: User) = task {
        use banOnReplyActivity = botActivity.StartActivity("unban")
        let targetUserId = userToUnban.Id
        %banOnReplyActivity
            .SetTag("vahterId", vahter.Id)
            .SetTag("vahterUsername", defaultArg vahter.Username null)
            .SetTag("targetId", targetUserId)
            .SetTag("targetUsername", userToUnban.Username)

        // delete ban record from event stream
        let actor = Actor.User {| userId = vahter.Id; username = vahter.Username |}
        do! db.RecordUserUnbanned(userToUnban.Id, actor)

        // try unban user in all monitored chats
        let! unbanResults = this.UnbanInAllChats(targetUserId)

        // produce aggregated log message
        let logMsg =
            aggregateResultInLogMsg
                (LoggedAction.Unban {| chat = msg.Chat; actor = actor; target = userToUnban |})
                logger
                unbanResults

        // log both to logger and to All Logs channel
        do! botClient.SendMessage(
                chatId = ChatId(botConfig.Value.AllLogsChannelId),
                text = logMsg
            ) |> taskIgnore
        logger.LogInformation logMsg
    }

    /// Checks user's social score and triggers auto-ban if below threshold
    member private this.CheckAndAutoBan(msg: TgMessage, actor: Actor) = task {
        if not botConfig.Value.MlSpamAutobanEnabled then
            return false
        else
            use banOnReplyActivity = botActivity.StartActivity("checkAndAutoBan")
            %banOnReplyActivity
                .SetTag("spammerId", msg.SenderId)
                .SetTag("spammerUsername", msg.SenderUsername)
                .SetTag("actor", caseName actor)

            let! userStats = db.GetUserStatsByLastNMessages(botConfig.Value.MlSpamAutobanCheckLastMsgCount, msg.SenderId)
            let socialScore = userStats.good - userStats.bad

            %banOnReplyActivity.SetTag("socialScore", socialScore)

            if double socialScore <= botConfig.Value.MlSpamAutobanScoreThreshold then
                // ban user in all monitored chats
                do! this.TotalBan(msg, actor)
                let logStr = $"Auto-banned user {prependUsername msg.SenderUsername} ({msg.SenderId}) due to the low social score {socialScore}"
                logger.LogInformation logStr
                fireAndForget logger "autoBanLogPost" (fun () ->
                    botClient.SendMessage(
                        chatId = ChatId(botConfig.Value.AllLogsChannelId),
                        text = logStr
                    ) :> Task
                )
                return true
            else
                return false
    }

    /// Deletes spam message, posts to detected spam channel with override button,
    /// and checks karma for auto-ban.
    member private this.DeleteSpam(msg: TgMessage, actor: Actor, reason: AutoDeleteReason) = task {
        use activity = botActivity.StartActivity("deleteSpam")
        %activity
            .SetTag("spammerId", msg.SenderId)
            .SetTag("spammerUsername", msg.SenderUsername)
            .SetTag("actor", caseName actor)

        // 1. Delete message + record BotAutoDeleted
        recordDeletedMessage msg.ChatId msg.ChatUsername "spamDeletion"
        do! botClient.DeleteMessage(ChatId(msg.ChatId), msg.MessageId)
            |> safeTaskAwait (fun e -> logger.LogWarning ($"Failed to delete message {msg.MessageId} from chat {msg.ChatId}", e))
        do! db.RecordBotAutoDeleted(msg.ChatId, msg.MessageId, msg.SenderId, reason)

        // 2. Post to detected spam channel with "NOT a spam" override button.
        // RecordCallbackCreated runs synchronously so a button click is always routable;
        // the channel post + RecordCallbackMessagePosted run in background. The cleanup
        // path tolerates action_message_id = NULL, so the brief race window is safe.
        let logMsg = $"Deleted spam ({formatReasonStr reason (Some actor)}) in {prependUsername msg.ChatUsername} ({msg.ChatId}) from {prependUsername msg.SenderUsername} ({msg.SenderId}) with text:\n{msg.Text}"
        let callbackId = Guid.NewGuid()
        do! db.RecordCallbackCreated(callbackId, CallbackMessage.NotASpam { message = msg.RawMessage }, msg.SenderId, botConfig.Value.DetectedSpamChannelId)
        let markup = InlineKeyboardMarkup [
            InlineKeyboardButton.WithCallbackData("✅ NOT a spam", string callbackId)
        ]
        fireAndForget logger "detectedSpamChannelPost" (fun () ->
            task {
                let! sent = botClient.SendMessage(
                    chatId = ChatId(botConfig.Value.DetectedSpamChannelId),
                    text = logMsg,
                    replyMarkup = markup
                )
                do! db.RecordCallbackMessagePosted(callbackId, sent.MessageId)
            } :> Task
        )

        // 3. All logs channel (readonly, no buttons)
        fireAndForget logger "allLogsChannelPost" (fun () ->
            botClient.SendMessage(
                chatId = ChatId(botConfig.Value.AllLogsChannelId),
                text = logMsg
            ) :> Task
        )
        logger.LogInformation logMsg

        // 4. Karma check + autoban
        let! _ = this.CheckAndAutoBan(msg, actor)
        ()
    }

    /// Reports uncertain spam to potential spam channel with KILL/SPAM/NOT SPAM buttons for human triage.
    /// Does NOT delete the message.
    member private _.ReportPotentialSpam(msg: TgMessage, reason: AutoDeleteReason) = task {
        use activity = botActivity.StartActivity("reportPotentialSpam")
        %activity
            .SetTag("spammerId", msg.SenderId)
            .SetTag("spammerUsername", msg.SenderUsername)

        let logMsg = $"Detected spam ({formatReasonStr reason None}) in {prependUsername msg.ChatUsername} ({msg.ChatId}) from {prependUsername msg.SenderUsername} ({msg.SenderId}) with text:\n{msg.Text}"

        // Create three callbacks for human triage
        let killId = Guid.NewGuid()
        let softSpamId = Guid.NewGuid()
        let notSpamId = Guid.NewGuid()
        do! db.RecordCallbackCreated(killId, CallbackMessage.Spam { message = msg.RawMessage }, msg.SenderId, botConfig.Value.PotentialSpamChannelId)
        do! db.RecordCallbackCreated(softSpamId, CallbackMessage.MarkAsSpam { message = msg.RawMessage }, msg.SenderId, botConfig.Value.PotentialSpamChannelId)
        do! db.RecordCallbackCreated(notSpamId, CallbackMessage.NotASpam { message = msg.RawMessage }, msg.SenderId, botConfig.Value.PotentialSpamChannelId)
        let markup = InlineKeyboardMarkup [|
            InlineKeyboardButton.WithCallbackData("🚫 KILL", string killId);
            InlineKeyboardButton.WithCallbackData("⚠️ SPAM", string softSpamId);
            InlineKeyboardButton.WithCallbackData("✅ NOT SPAM", string notSpamId)
        |]
        let! sent = botClient.SendMessage(
            chatId = ChatId(botConfig.Value.PotentialSpamChannelId),
            text = logMsg,
            replyMarkup = markup
        )
        for callbackId in [killId; softSpamId; notSpamId] do
            do! db.RecordCallbackMessagePosted(callbackId, sent.MessageId)

        // All logs channel (readonly, no buttons)
        do! botClient.SendMessage(
                chatId = ChatId(botConfig.Value.AllLogsChannelId),
                text = logMsg
            ) |> taskIgnore
        logger.LogInformation logMsg
    }

    // ── Reaction-spam triage pipeline ──────────────────────────────────────

    /// Iterates every reaction this user has placed (optionally limited to one chat) and
    /// removes each via DeleteMessageReaction. Old events (pre-2026-05) without chatId/messageId
    /// are silently skipped — the schema enrichment ensures everything recorded after this PR
    /// can be cleaned up. Tolerant of "already removed" errors (Telegram returns OK or 400).
    member private _.RemoveRecordedReactions(userId: int64, chatFilter: int64 option) = task {
        let! targets = db.GetReactionTargetsForUser(userId, chatFilter)
        do!
            targets
            |> Seq.map (fun t -> task {
                try
                    do! botClient.DeleteMessageReaction(ChatId t.chat_id, t.message_id, userId)
                with e ->
                    logger.LogInformation(e, "DeleteMessageReaction failed for user {U} msg {C}/{M} (likely already gone)", userId, t.chat_id, t.message_id)
            })
            |> Task.WhenAll
            |> taskIgnore
        return targets.Length
    }

    /// SPAM verdict action: restrict future reactions in this chat + remove existing ones.
    /// Restriction comes first so the spammer can't immediately re-react during the deletion loop.
    /// `actor` flows into the AllLogs audit message so vahters can see who decided.
    member private this.ReactionAct_Spam(chatId: int64, targetUser: User, actor: Actor) = task {
        try
            let perms = ChatPermissions(CanReactToMessages = false)
            do! botClient.RestrictChatMember(ChatId chatId, targetUser.Id, perms)
        with e ->
            logger.LogWarning(e, "RestrictChatMember(can_react=false) failed for user {U} in chat {C}", targetUser.Id, chatId)
        let! removed = this.RemoveRecordedReactions(targetUser.Id, Some chatId)

        let chatLabel =
            botConfig.Value.ChatsToMonitor
            |> Seq.tryFind (fun kv -> kv.Value = chatId)
            |> Option.map (fun kv -> sprintf "%s (%d)" (prependUsername kv.Key) chatId)
            |> Option.defaultValue (sprintf "(unknown chat %d)" chatId)
        let sanitizedUsername = defaultArg targetUser.Username null |> prependUsername
        let logMsg =
            $"⚠️ Reaction-triage SPAM on {sanitizedUsername} ({targetUser.Id}) in {chatLabel} by {prependUsername actor.DisplayName} — restricted reactions, removed {removed} existing"
        do! botClient.SendMessage(ChatId botConfig.Value.AllLogsChannelId, logMsg) |> taskIgnore
        logger.LogInformation logMsg
        return removed
    }

    /// BAN verdict action: clean up reactions everywhere, delete user's messages, ban in all chats.
    /// `actor` is whoever triggered the ban — Actor.LLM in autonomous mode, Actor.User when a
    /// vahter clicked the button. The actor flows into the UserBanned event so analytics /
    /// stats queries can attribute the kill correctly.
    member private this.ReactionAct_Ban(triggeringChatId: int64, triggeringMessageId: int, targetUser: User, actor: Actor) = task {
        let! removedReactions = this.RemoveRecordedReactions(targetUser.Id, None)

        // delete all recorded messages from user in all chats
        let! allUserMessages = db.GetUserMessages(targetUser.Id)
        do!
            allUserMessages
            |> Seq.map (fun msg -> task {
                try
                    recordDeletedMessage msg.chat_id null "reactionTriage_ban_history"
                    do! botClient.DeleteMessage(ChatId msg.chat_id, msg.message_id)
                with e ->
                    logger.LogWarning(e, "Failed to delete message {M} from chat {C}", msg.message_id, msg.chat_id)
            })
            |> Task.WhenAll
            |> taskIgnore

        let! banResults = this.BanInAllChats(targetUser.Id)

        // Record auto-deletion and ban events (cross-stream, not atomic — see totalBan comment)
        do! db.RecordBotAutoDeleted(triggeringChatId, triggeringMessageId, targetUser.Id, AutoDeleteReason.ReactionSpam {| reactionCount = targetUser.ReactionCount |})
        do! db.RecordUserBannedNoMessage(targetUser.Id, actor, triggeringChatId, triggeringMessageId, botConfig.Value.BanExpiryDays)
        bannedUsersCounter.Add(1L, tagsForVahter actor)

        let sanitizedUsername = defaultArg targetUser.Username null |> prependUsername
        let allChatsOk = banResults |> Array.forall Result.isOk
        let logMsgBuilder = StringBuilder()
        %logMsgBuilder.Append $"🤖 Reaction-triage BAN of {sanitizedUsername} ({targetUser.Id}) by {prependUsername actor.DisplayName}"
        %logMsgBuilder.AppendLine $" — removed {removedReactions} reactions, deleted {allUserMessages.Length} messages, banned in all chats"
        if not allChatsOk then
            %logMsgBuilder.AppendLine "Ban results:"
            for result in banResults do
                match result with
                | Ok(chatName, _) -> %logMsgBuilder.AppendLine $"  ✅ {prependUsername chatName}"
                | Error(chatName, _, e) -> %logMsgBuilder.AppendLine $"  ❌ {prependUsername chatName}: {e.Message}"
        let logMsg = string logMsgBuilder
        do! botClient.SendMessage(ChatId botConfig.Value.AllLogsChannelId, logMsg) |> taskIgnore
        logger.LogInformation logMsg
    }

    /// NOT_SPAM verdict action: just set the cooldown — no destructive operation.
    /// `actor` flows into the AllLogs audit message so vahters can see who decided.
    member private _.ReactionAct_NotSpam(targetUser: User, actor: Actor) = task {
        let cooldownDays = botConfig.Value.ReactionNotSpamCooldownDays
        let until = utcNow().AddDays(float cooldownDays)
        do! db.RecordReactionTriageNotSpam(targetUser.Id, until, actor)

        let sanitizedUsername = defaultArg targetUser.Username null |> prependUsername
        let logMsg =
            $"✅ Reaction-triage NOT SPAM on {sanitizedUsername} ({targetUser.Id}) by {prependUsername actor.DisplayName} — cooldown for {cooldownDays}d, no destructive action"
        do! botClient.SendMessage(ChatId botConfig.Value.AllLogsChannelId, logMsg) |> taskIgnore
        logger.LogInformation logMsg
    }

    /// First-click-wins for reaction triage: when one vahter resolves a suspect, sweep ALL
    /// other reaction-triage alerts for the same user (deduped by alert message_id) so a
    /// second vahter can't disagree on a leftover button. Mirrors the cleanupCallbacksTask
    /// pattern in TotalBan, scoped to reaction-triage callbacks only (Detected-Spam cards
    /// from ML auto-bans aren't ours to delete).
    member private _.CleanupReactionTriageCallbacksForUser(userId: int64) = task {
        let! callbacks = db.GetActiveReactionTriageCallbacksByUserId(userId)
        if callbacks.Length > 0 then
            logger.LogInformation($"Reaction triage: cleaning up {callbacks.Length} sibling callbacks for user {userId}")

            // Delete each unique alert message from the action channel exactly once.
            // (Each alert posts 3 callbacks sharing one message_id; we only want one
            // delete call per alert.)
            let uniqueMessages =
                callbacks
                |> Array.choose (fun c ->
                    match c.action_message_id with
                    | Some msgId -> Some (c.action_channel_id, msgId)
                    | None       -> None)
                |> Array.distinct

            do!
                uniqueMessages
                |> Seq.map (fun (chId, msgId) -> task {
                    do! botClient.DeleteMessage(ChatId chId, msgId)
                        |> safeTaskAwait (fun e -> logger.LogWarning($"Failed to delete reaction-triage alert {msgId} in chat {chId}", e))
                })
                |> Task.WhenAll
                |> taskIgnore

            // Expire every callback (channel-delete is best-effort; DB state is authoritative
            // for "first click wins" — subsequent clicks will resolve to "already processed").
            for callback in callbacks do
                do! db.ExpireCallback(callback.id)
    }

    /// Builds the dossier shown to both the LLM and (in shadow / UNSURE) the vahter.
    /// Privacy-strict users return None photo and empty bio — both downstream consumers handle that.
    member private _.BuildReactionTriageDossier(reaction: MessageReactionUpdated, targetUser: User) = task {
        let! profile = profileFetcher.Fetch(reaction.User.Id)
        let! totalMessages = db.GetTotalMessagesByUser(reaction.User.Id)
        let! firstSeen = db.GetUserFirstSeenAt(reaction.User.Id)
        let! events = db.GetRecentDossierEvents(reaction.User.Id, 10)
        let displayName =
            let parts = [
                Option.ofObj reaction.User.FirstName |> Option.defaultValue ""
                Option.ofObj reaction.User.LastName  |> Option.defaultValue ""
            ]
            parts |> String.concat " " |> fun s -> s.Trim()
        return
            { UserId                   = reaction.User.Id
              Username                 = targetUser.Username
              DisplayName              = displayName
              Bio                      = profile.Bio
              PhotoBytes               = profile.PhotoBytes
              TotalMessagesAcrossChats = totalMessages
              FirstSeenAt              = firstSeen
              Last10Events             = events
              OriginatingChatId        = reaction.Chat.Id }
    }

    /// Posts the admin alert with full dossier + photo + 3 callback buttons (BAN / SPAM / NOT SPAM).
    /// Uses HTML parse mode. When the suspect has a username, write @username — Telegram
    /// auto-renders that as a clickable profile mention with no `<a>` tag needed. For users
    /// without a username we fall back to an explicit `tg://user?id=…` link.
    member private _.PostReactionTriageAlert(dossier: ReactionTriageDossier, llmVerdict: string option, llmReason: string option, annotationLine: string) = task {
        let htmlEscape (s: string) =
            if isNull s then ""
            else s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")

        // Short, human-readable chat label using ChatsToMonitor config. Prepends "@" so
        // Telegram auto-links public chats; appends the numeric id in parens so vahters
        // can copy-paste it for SQL/event-stream lookups. Falls back to "(unknown)" when
        // the chat isn't in our config (e.g. legacy events without chatId).
        let chatLabel (chatId: int64) =
            botConfig.Value.ChatsToMonitor
            |> Seq.tryFind (fun kv -> kv.Value = chatId)
            |> Option.map (fun kv -> sprintf "%s (%d)" (prependUsername kv.Key) chatId)
            |> Option.defaultValue (sprintf "(unknown chat %d)" chatId)

        // Suspect rendering — always include the numeric user id in parens so vahters
        // can grep logs / event streams / DB by it:
        //  - username present → "@handle (id)"  (Telegram auto-links the @handle)
        //  - username missing → "<a href='tg://user?id=…'>Display Name</a> (id)"  (only path that
        //    links to a profile when there's no public handle)
        let suspectLink =
            match dossier.Username with
            | Some u -> sprintf "@%s (%d)" (htmlEscape u) dossier.UserId
            | None ->
                let name = htmlEscape dossier.DisplayName
                let displayed = if String.IsNullOrWhiteSpace name then "(no name)" else name
                sprintf "<a href=\"tg://user?id=%d\">%s</a> (%d)" dossier.UserId displayed dossier.UserId
        let firstSeen =
            match dossier.FirstSeenAt with
            | Some t ->
                let days = (utcNow() - t).TotalDays |> int
                sprintf "%s (%dd ago)" (t.ToString("yyyy-MM-dd")) days
            | None -> "(never)"
        let bioLine =
            if String.IsNullOrWhiteSpace dossier.Bio then "<i>(empty / privacy-strict)</i>"
            else htmlEscape dossier.Bio

        let eventLines =
            if dossier.Last10Events.Length = 0 then "  <i>(no recent events on record)</i>"
            else
                dossier.Last10Events
                |> Array.map (fun e ->
                    let ts = e.created_at.ToString("MM-dd HH:mm")
                    // Old reaction events (pre-PR) lack chatId and surface here as chat_id = 0.
                    // Don't fake a real chat — say "(unknown)" so the dossier doesn't lie.
                    let chat =
                        if e.chat_id = 0L then "<i>(unknown)</i>"
                        else chatLabel e.chat_id
                    match e.kind with
                    | "reaction" ->
                        let emojiPart =
                            if isNull e.emoji || e.emoji = "" then ""
                            else " " + htmlEscape e.emoji
                        sprintf "  • %s  %s  reacted%s" ts chat emojiPart
                    | _ ->
                        let txt =
                            if isNull e.text then "(no text)"
                            elif e.text.Length > 60 then e.text.Substring(0, 60) + "…"
                            else e.text
                        sprintf "  • %s  %s  \"%s\"" ts chat (htmlEscape txt))
                |> String.concat "\n"

        let header =
            sprintf
                "🚨 <b>Reaction-spam triage</b>\n%s\n\n<b>Suspect:</b> %s\n<b>First seen:</b> %s · <b>Msgs across chats:</b> %d\n\n<b>Bio:</b> %s\n\n<b>Recent activity:</b>\n%s\n\n<b>Originating chat:</b> %s"
                (htmlEscape annotationLine)
                suspectLink
                firstSeen dossier.TotalMessagesAcrossChats
                bioLine
                eventLines
                (chatLabel dossier.OriginatingChatId)

        let banId      = Guid.NewGuid()
        let spamId     = Guid.NewGuid()
        let notSpamId  = Guid.NewGuid()
        let ctx =
            { userId     = dossier.UserId
              chatId     = dossier.OriginatingChatId
              llmVerdict = llmVerdict
              llmReason  = llmReason }
        // Callbacks route to PotentialSpamChannel — the "zero-inbox" actionable channel.
        // AllLogsChannel gets a non-interactive mirror further down for audit trail.
        let actionChannelId = botConfig.Value.PotentialSpamChannelId
        do! db.RecordCallbackCreated(banId,     CallbackMessage.ReactionBan ctx,     dossier.UserId, actionChannelId)
        do! db.RecordCallbackCreated(spamId,    CallbackMessage.ReactionSpam ctx,    dossier.UserId, actionChannelId)
        do! db.RecordCallbackCreated(notSpamId, CallbackMessage.ReactionNotSpam ctx, dossier.UserId, actionChannelId)

        let markup = InlineKeyboardMarkup [|
            InlineKeyboardButton.WithCallbackData("🚫 BAN",      string banId)
            InlineKeyboardButton.WithCallbackData("⚠️ SPAM",     string spamId)
            InlineKeyboardButton.WithCallbackData("✅ NOT SPAM", string notSpamId)
        |]

        // Telegram caption limit is 1024 chars; sendMessage limit is 4096. With the trimmed
        // layout (no "spam signals" footer, compact chat names) a 10-event dossier is ~700–900
        // chars, comfortably under the photo-caption cap. If a freakishly long bio pushes us
        // over, fall back to text-only (sendMessage with link preview disabled) so vahter still
        // sees everything.
        let actionChatId = ChatId actionChannelId
        let captionFits = header.Length <= 1000
        let! sent =
            match dossier.PhotoBytes with
            | Some bytes when captionFits ->
                use ms = new MemoryStream(bytes)
                let inputFile = InputFileStream(ms, "profile.jpg")
                botClient.SendPhoto(actionChatId, inputFile, caption = header, parseMode = ParseMode.Html, replyMarkup = markup)
            | _ ->
                botClient.SendMessage(actionChatId, header, parseMode = ParseMode.Html, replyMarkup = markup)

        for callbackId in [banId; spamId; notSpamId] do
            do! db.RecordCallbackMessagePosted(callbackId, sent.MessageId)

        // Mirror the full alert text (no buttons, no photo) to AllLogsChannel for audit.
        do! botClient.SendMessage(ChatId botConfig.Value.AllLogsChannelId, header, parseMode = ParseMode.Html) |> taskIgnore

        logger.LogInformation("Reaction triage alert posted for user {U} chat {C} (LLM verdict: {V})", dossier.UserId, dossier.OriginatingChatId, defaultArg llmVerdict "(none)")
    }

    /// Top-level orchestrator: builds dossier, calls LLM (if enabled), records the verdict event,
    /// then either acts autonomously or posts the vahter alert.
    member private this.RunReactionTriagePipeline(reaction: MessageReactionUpdated, targetUser: User) = task {
        use activity = botActivity.StartActivity("reactionTriagePipeline")
        %activity
            .SetTag("user_id", targetUser.Id)
            .SetTag("chat_id", reaction.Chat.Id)

        let! dossier = this.BuildReactionTriageDossier(reaction, targetUser)

        let shadowDisabled = botConfig.Value.LlmReactionTriageShadowDisable
        let autoAct        = botConfig.Value.LlmReactionTriageAutoAct
        let shadowMode     = not autoAct

        // Run LLM unless shadow is disabled. In shadow mode the verdict is recorded and surfaced to
        // vahters as an annotation but does NOT change the action — vahter button decides.
        let! llmResult =
            if shadowDisabled then
                task { return None }
            else
                task {
                    use cts = new CancellationTokenSource(TimeSpan.FromSeconds 60.)
                    let! r = reactionTriage.ClassifyReactionSpammer(dossier, shadowMode, cts.Token)
                    return Some r
                }

        let llmVerdictStr =
            llmResult |> Option.map (fun r -> r.Verdict.ToWireString())
        let llmReason =
            llmResult |> Option.bind (fun r -> r.Reason)

        // Decide path based on autoAct flag and verdict
        let goAutonomous =
            match autoAct, llmResult with
            | true, Some r ->
                match r.Verdict with
                | LlmReactionVerdict.Ban
                | LlmReactionVerdict.Spam
                | LlmReactionVerdict.NotSpam -> true
                | LlmReactionVerdict.Unsure
                | LlmReactionVerdict.Error   -> false
            | _ -> false

        if goAutonomous then
            let actor = Actor.LLM {| modelName = (Option.get llmResult).ModelName; promptHash = (Option.get llmResult).PromptHash |}
            match (Option.get llmResult).Verdict with
            | LlmReactionVerdict.Ban     -> do! this.ReactionAct_Ban(reaction.Chat.Id, reaction.MessageId, targetUser, actor)
            | LlmReactionVerdict.Spam    -> let! _ = this.ReactionAct_Spam(reaction.Chat.Id, targetUser, actor) in ()
            | LlmReactionVerdict.NotSpam -> do! this.ReactionAct_NotSpam(targetUser, actor)
            | LlmReactionVerdict.Unsure
            | LlmReactionVerdict.Error   -> ()  // unreachable per goAutonomous guard
        else
            let annotationLine =
                match shadowDisabled, llmResult with
                | true, _ -> "LLM disabled — vahter decides"
                | false, Some r ->
                    match r.Verdict with
                    | LlmReactionVerdict.Error  -> sprintf "LLM annotation unavailable (%s)" (defaultArg r.Reason "error")
                    | LlmReactionVerdict.Unsure -> sprintf "LLM was UNSURE — \"%s\"" (defaultArg r.Reason "")
                    | _ ->
                        let modeLabel = if shadowMode then "shadow" else "autoAct"
                        sprintf "LLM (%s) said: %s — \"%s\"" modeLabel (r.Verdict.ToWireString()) (defaultArg r.Reason "")
                | false, None -> "LLM not called"
            do! this.PostReactionTriageAlert(dossier, llmVerdictStr, llmReason, annotationLine)
    }

    // -----------------------------------------------------------------------
    // Private members — ML / LLM auto-verdict
    // -----------------------------------------------------------------------

    /// Fast text-only ML check used to short-circuit before Azure OCR.
    /// Returns Some score when ML alone is confident the message is spam
    /// (records the ML score so callers don't double-record). Returns None
    /// for null text, low ML score, warning band, or old-user immunity —
    /// in which case the caller should run Azure OCR for any cache misses
    /// and then call GetAutoVerdict for the full verdict.
    member private _.PreOcrMlCheck(msg: TgMessage, usrMsgCount: int) = task {
        if isNull msg.Text then return None
        else
        match ml.Predict(msg.Text, usrMsgCount, msg.Entities) with
        | None -> return None
        | Some prediction ->
            // Old-user immunity: defer to GetAutoVerdict so the
            // "ML god shows mercy" log fires there exactly once.
            if prediction.Score > 0f && usrMsgCount >= botConfig.Value.MlOldUserMsgCount then
                return None
            elif prediction.Score >= botConfig.Value.MlSpamThreshold then
                do! db.RecordMlScoredMessage(msg.ChatId, msg.MessageId, float prediction.Score, true)
                return Some (float prediction.Score)
            else
                return None
    }

    /// Runs ML prediction + optional LLM triage, returns verdict.
    member private _.GetAutoVerdict(msg: TgMessage, usrMsgCount: int) = task {
        match ml.Predict(msg.Text, usrMsgCount, msg.Entities) with
        | None -> return None
        | Some prediction ->
            do! db.RecordMlScoredMessage(msg.ChatId, msg.MessageId, float prediction.Score, prediction.Score >= botConfig.Value.MlSpamThreshold)

            // Old user immunity: skip triage when ML score is positive but user has enough history
            if prediction.Score > 0f && usrMsgCount >= botConfig.Value.MlOldUserMsgCount then
                let logMsg = $"User {prependUsername msg.SenderUsername} ({msg.SenderId}) has {usrMsgCount} msgs (score: {prediction.Score}) — ML god shows mercy today, skipping triage"
                do! botClient.SendMessage(ChatId(botConfig.Value.AllLogsChannelId), text = logMsg) |> taskIgnore
                logger.LogInformation logMsg
                return Some (AutoVerdict.NotSpam Actor.ML)
            elif prediction.Score >= botConfig.Value.MlSpamThreshold then
                return Some (AutoVerdict.Spam (float prediction.Score, Actor.ML))
            elif prediction.Score >= botConfig.Value.MlWarningThreshold then
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

    member private this.ProcessMessage(msg: TgMessage) = task {
        // Records the message exactly once, with whatever enrichment finished
        // by the time we call it. Each branch below calls this at the right
        // point so the persisted text matches the text we classified on, and
        // CheckAndAutoBan / GetUserStatsByLastNMessages see the current msg
        // before any ban-side-effects fire.
        let mutable recorded = false
        let recordMsg () = task {
            if not recorded then
                recorded <- true
                if msg.IsEdit then do! db.EditMessage(msg)
                else do! db.InsertMessage(msg)
        }

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
                | MessageEntityType.TextMention | MessageEntityType.Mention ->
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

        // Azure OCR is now deferred — if the cache step left any photo source
        // un-applied, classification still has work to do even when msg.Text
        // is null at this point (it might become non-null after Azure runs).
        let hasPendingAzureOcr =
            botConfig.Value.OcrEnabled
            && ((not msg.OwnPhotoOcrApplied
                 && not (isNull msg.Photos)
                 && msg.Photos.Length > 0)
                || (botConfig.Value.ForwardSpamDetectionEnabled
                    && not msg.ExternalReplyPhotoOcrApplied
                    && not (isNull msg.ExternalReply)
                    && not (isNull msg.ExternalReply.Photo)
                    && msg.ExternalReply.Photo.Length > 0))

        if containsInvisibleMention then
            do! recordMsg()
            do! this.DeleteSpam(msg, botConfig.Value.BotActor, InvisibleMention)

        elif botConfig.Value.MlEnabled && (msg.Text <> null || hasPendingAzureOcr) then
            use mlActivity = botActivity.StartActivity("mlPrediction")

            let shouldBeSkipped =
                // skip prediction for automatic forwards from linked channels
                if msg.IsAutomaticForward then
                    true
                // skip prediction for channel senders whose channel is in ChatsToMonitor
                elif msg.IsChannelSender
                     && botConfig.Value.ChatsToMonitor.ContainsValue msg.SenderId then
                    true
                // skip prediction for vahters or local admins
                elif botConfig.Value.AllowedUsers.ContainsValue msg.SenderId
                     || UpdateChatAdmins.Admins.Contains msg.SenderId then
                    true
                else

                match botConfig.Value.MlStopWordsInChats.TryGetValue msg.ChatId with
                | true, stopWords when not (isNull msg.Text) ->
                    stopWords
                    |> Seq.exists (fun sw -> msg.Text.Contains(sw, StringComparison.OrdinalIgnoreCase))
                | _ -> false
            %mlActivity.SetTag("skipPrediction", shouldBeSkipped)

            if not shouldBeSkipped then
                // ml.Predict only branches on usrMsgCount via two thresholds:
                // `>= MlOldUserMsgCount` and `< MlTrainCriticalMsgCount`. Anything
                // beyond the larger one is indistinguishable, so cap the read.
                let mlCountCap =
                    (max botConfig.Value.MlOldUserMsgCount botConfig.Value.MlTrainCriticalMsgCount) + 1
                let! usrMsgCount = db.CountUniqueUserMsgsUpTo(msg.SenderId, mlCountCap)

                %mlActivity.SetTag("preOcrTextLength", if isNull msg.Text then 0 else msg.Text.Length)
                %mlActivity.SetTag("ownPhotoOcrAppliedBeforeCheck", msg.OwnPhotoOcrApplied)
                %mlActivity.SetTag("externalReplyPhotoOcrAppliedBeforeCheck", msg.ExternalReplyPhotoOcrApplied)

                let! confidentSpam = this.PreOcrMlCheck(msg, usrMsgCount)
                match confidentSpam with
                | Some score ->
                    %mlActivity.SetTag("spamScoreMl", score)
                    %mlActivity.SetTag("preOcrShortCircuit", true)
                    logger.LogInformation(
                        "Pre-OCR short-circuit: classified message {MessageId} as spam from text alone (score={Score:F3}); skipping Azure OCR",
                        msg.MessageId, score)
                    do! recordMsg()
                    let reason = MlSpam {| score = score |}
                    if botConfig.Value.MlSpamDeletionEnabled then
                        do! this.DeleteSpam(msg, Actor.ML, reason)
                    else
                        do! this.ReportPotentialSpam(msg, reason)
                | None ->
                    %mlActivity.SetTag("preOcrShortCircuit", false)
                    // Text alone wasn't enough — pay for Azure OCR on the
                    // cache misses, then re-classify with ML+LLM. Both
                    // enrichment calls are no-ops when their flag is set
                    // and they swallow their own exceptions, so OCR
                    // failures never block the ML classifier from running.
                    do! this.TryEnrichWithForwardedPhotoOcr(msg)
                    do! this.TryEnrichWithOcr(msg)
                    %mlActivity.SetTag("postOcrTextLength", if isNull msg.Text then 0 else msg.Text.Length)
                    do! recordMsg()
                    let! autoVerdict = this.GetAutoVerdict(msg, usrMsgCount)
                    match autoVerdict with
                    | Some (AutoVerdict.Spam (score, actor)) ->
                        %mlActivity.SetTag("spamScoreMl", score)
                        let reason = MlSpam {| score = score |}
                        if botConfig.Value.MlSpamDeletionEnabled then
                            do! this.DeleteSpam(msg, actor, reason)
                        else
                            do! this.ReportPotentialSpam(msg, reason)
                    | Some (AutoVerdict.Uncertain score) ->
                        %mlActivity.SetTag("spamScoreMl", score)
                        do! this.ReportPotentialSpam(msg, MlSpam {| score = score |})
                    | Some (AutoVerdict.NotSpam _) | None ->
                        ()

        // Catch-all: every message that reached ProcessMessage must be recorded.
        // Idempotent — already-recorded branches above no-op here.
        do! recordMsg()
    }

    member private this.JustMessage(msg: TgMessage) = task {
        use _ =
            botActivity
                .StartActivity("justMessage")
                .SetTag("fromUserId", msg.SenderId)
                .SetTag("fromUsername", msg.SenderUsername)

        // check if user got auto-banned already
        let! user = db.GetUserById(msg.SenderId)
        if user |> Option.exists (fun u -> u.IsBanned(botConfig.Value.BanExpiryDays, utcNow())) then
            // already-banned path: record (with whatever text OnUpdate-time enrichment
            // produced — Azure OCR is deferred and won't run for banned users) and delete.
            if msg.IsEdit then do! db.EditMessage(msg)
            else do! db.InsertMessage(msg)

            // just delete message and move on
            let logMsg = $"Bot deleted message {msg.MessageId} from {prependUsername msg.SenderUsername}({msg.SenderId}) in {prependUsername msg.ChatUsername}({msg.ChatId}) because user was already banned"
            logger.LogInformation logMsg
            do! botClient.SendMessage(
                    chatId = ChatId(botConfig.Value.AllLogsChannelId),
                    text = logMsg
                ) |> taskIgnore
            recordDeletedMessage msg.ChatId msg.ChatUsername "alreadyAutoBanned"
            do! botClient.DeleteMessage(ChatId(msg.ChatId), msg.MessageId)
                |> safeTaskAwait (fun e -> logger.LogWarning ($"Failed to delete message {msg.MessageId} from chat {msg.ChatId}", e))

        else do! this.ProcessMessage(msg)
    }

    // -----------------------------------------------------------------------
    // Private members — Admin commands
    // -----------------------------------------------------------------------

    member private this.AdminCommand(vahter: User, msg: TgMessage) =
        // aux functions to overcome annoying FS3511: This state machine is not statically compilable.
        let banOnReplyAux() = task {
            let authed =
                isBanAuthorized
                    botConfig.Value
                    msg.ReplyToMessage.Value
                    vahter
                    logger
            if authed then
                do! this.BanOnReply(msg, vahter)
        }
        let unbanAux() = task {
            if isUserVahter botConfig.Value vahter then
                let targetUserId = msg.Text.Split(" ", StringSplitOptions.RemoveEmptyEntries)[1] |> int64
                let! userToUnban = db.GetUserById(targetUserId)
                match userToUnban with
                | None ->
                    logger.LogWarning $"User {vahter.Username} ({vahter.Id}) tried to unban non-existing user {targetUserId}"
                | Some userToUnban ->
                    do! this.Unban(msg, vahter, userToUnban)
        }
        let softBanOnReplyAux() = task {
            let authed =
                isBanAuthorized
                    botConfig.Value
                    msg.ReplyToMessage.Value
                    vahter
                    logger
            if authed then
                do! this.SoftBanMsg(msg, vahter)
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
                do! this.Ping(msg)
            do! deleteCmdTask
        }

    // -----------------------------------------------------------------------
    // Private members — Message handling
    // -----------------------------------------------------------------------

    member private this.OnMessage(msg: TgMessage) = task {
        use banOnReplyActivity = botActivity.StartActivity("onMessage")

        // early return if we can't process it
        if not msg.HasSender then
            logger.LogWarning "Received message without resolvable sender"
        else

        // early return if we don't monitor this chat
        if not (botConfig.Value.ChatsToMonitor.ContainsValue msg.ChatId) then
            ()
        else

        %banOnReplyActivity
            .SetTag("chatId", msg.ChatId)
            .SetTag("chatUsername", msg.ChatUsername)

        // metrics: count every processed message per chat
        messagesProcessedCounter.Add(1L, tagsForChat msg.ChatId msg.ChatUsername)

        // upserting user to DB
        let! user =
            db.UpsertUser(msg.SenderId, Option.ofObj msg.SenderUsername)

        // check if message is a known command from authorized user
        if isKnownCommand msg && isUserVahter botConfig.Value user then
            do! this.AdminCommand(user, msg)

        // if message is not a command from authorized user, just save it ID to DB
        else
            do! this.JustMessage(msg)
    }

    // -----------------------------------------------------------------------
    // Private members — OCR enrichment
    // -----------------------------------------------------------------------

    member private this.OcrFresh(largestPhoto: PhotoSize, fileUniqueId: string) = task {
        let! file = botClient.GetFile(largestPhoto.FileId)

        if String.IsNullOrWhiteSpace file.FilePath then
            logger.LogWarning("Failed to resolve file path for photo {PhotoId}", largestPhoto.FileId)
            return None
        else
            let apiBase = if isNull botConfig.Value.TelegramApiBaseUrl then "https://api.telegram.org" else botConfig.Value.TelegramApiBaseUrl
            let fileUrl = $"{apiBase}/file/bot{botConfig.Value.BotToken}/{file.FilePath}"
            let! analysis = computerVision.AnalyzeImageUrl fileUrl
            match analysis with
            | null ->
                // Azure failed (network / non-2xx / disabled). Don't cache failures.
                return None
            | a ->
                if not (String.IsNullOrWhiteSpace fileUniqueId) then
                    try do! ocrCache.Save(fileUniqueId, a.RawJson, a.Text)
                    with ex -> logger.LogWarning(ex, "Failed to save OCR cache for {FileUniqueId}", fileUniqueId)
                if String.IsNullOrWhiteSpace a.Text then return None
                else return Some a.Text
    }

    member private _.SelectOcrPhoto(photos: PhotoSize array) =
        let candidates =
            photos
            |> Array.filter (fun p ->
                let size = int64 p.FileSize
                size = 0L || size <= botConfig.Value.OcrMaxFileSizeBytes)
        if candidates.Length = 0 then None
        else Some (selectLargestPhoto candidates)

    /// Cache-only OCR lookup. Returns:
    ///   None       — no candidate photo, no fileUniqueId, or cache miss
    ///   Some text  — cache hit. Text may be empty, meaning "OCR'd before, no text"
    member private this.OcrLookupCache(photos: PhotoSize array) = task {
        match this.SelectOcrPhoto(photos) with
        | None -> return None
        | Some largestPhoto ->
            let fileUniqueId = largestPhoto.FileUniqueId
            if String.IsNullOrWhiteSpace fileUniqueId then return None
            else
                let activity = Activity.Current
                if not (isNull activity) then
                    %activity.SetTag("ocr.fileUniqueId", fileUniqueId)
                return! ocrCache.TryGetText(fileUniqueId)
    }

    /// Azure-only OCR. Caller is expected to have checked the cache.
    /// OcrFresh still saves to cache on success (and tolerates empty fileUniqueId).
    member private this.OcrPhotosFresh(photos: PhotoSize array, messageId: int) = task {
        match this.SelectOcrPhoto(photos) with
        | None ->
            logger.LogWarning(
                "No photos under OCR limit of {LimitBytes} bytes for message {MessageId}",
                botConfig.Value.OcrMaxFileSizeBytes,
                messageId)
            return None
        | Some largestPhoto ->
            return! this.OcrFresh(largestPhoto, largestPhoto.FileUniqueId)
    }

    /// Cheap, synchronous, no I/O. Safe to call at the top of OnUpdate.
    member private _.TryEnrichWithForwardedQuoteText(msg: TgMessage) =
        if botConfig.Value.ForwardSpamDetectionEnabled
           && isMessageFromAllowedChats botConfig.Value msg
           && not (isNull msg.Quote)
           && not (String.IsNullOrWhiteSpace msg.Quote.Text) then
            use activity = botActivity.StartActivity("forwardedQuoteEnrichment")
            msg.PrependText(msg.Quote.Text)
            %activity.SetTag("quoteTextLength", msg.Quote.Text.Length)

    /// Cache-only OCR enrichment for any photos on the message — both the
    /// message's own photos and external-reply quote photos. No Azure calls.
    /// On a hit, marks the corresponding `…OcrApplied` flag so the deferred
    /// Azure step skips that source. On any error (e.g. postgres outage on
    /// the cache table) the flag stays false so the deferred step can retry.
    member private this.TryEnrichOcrFromCache(msg: TgMessage) = task {
        if not botConfig.Value.OcrEnabled then ()
        elif not (isMessageFromAllowedChats botConfig.Value msg) then ()
        else
            use activity = botActivity.StartActivity("ocrCacheLookup")

            // External-reply quote photo (prepend, to match the order produced
            // by the previous TryEnrichWithForwardedContent: quote + ocr).
            if botConfig.Value.ForwardSpamDetectionEnabled
               && not (isNull msg.ExternalReply)
               && not (isNull msg.ExternalReply.Photo)
               && msg.ExternalReply.Photo.Length > 0 then
                %activity.SetTag("externalReplyCacheLookup", true)
                try
                    let! cached = this.OcrLookupCache(msg.ExternalReply.Photo)
                    match cached with
                    | Some text ->
                        if not (String.IsNullOrWhiteSpace text) then
                            msg.PrependText(text)
                            %activity.SetTag("externalReplyCacheHit", "text")
                            %activity.SetTag("externalReplyCacheTextLength", text.Length)
                        else
                            %activity.SetTag("externalReplyCacheHit", "empty")
                        msg.ExternalReplyPhotoOcrApplied <- true
                    | None ->
                        %activity.SetTag("externalReplyCacheHit", "miss")
                with ex ->
                    logger.LogWarning(ex, "OCR cache lookup failed for external-reply photo on message {MessageId}; will fall back to Azure", msg.MessageId)
                    %activity.SetTag("externalReplyCacheHit", "error")

            // Message's own photos (append).
            if not (isNull msg.Photos) && msg.Photos.Length > 0 then
                %activity.SetTag("ownPhotoCacheLookup", true)
                try
                    let! cached = this.OcrLookupCache(msg.Photos)
                    match cached with
                    | Some text ->
                        if not (String.IsNullOrWhiteSpace text) then
                            msg.AppendText(text)
                            %activity.SetTag("ownPhotoCacheHit", "text")
                            %activity.SetTag("ownPhotoCacheTextLength", text.Length)
                        else
                            %activity.SetTag("ownPhotoCacheHit", "empty")
                        msg.OwnPhotoOcrApplied <- true
                    | None ->
                        %activity.SetTag("ownPhotoCacheHit", "miss")
                with ex ->
                    logger.LogWarning(ex, "OCR cache lookup failed for own photo on message {MessageId}; will fall back to Azure", msg.MessageId)
                    %activity.SetTag("ownPhotoCacheHit", "error")
    }

    /// Azure-only fallback for external-reply quote photos. Runs only when the
    /// cache step left ExternalReplyPhotoOcrApplied=false (cache miss). Errors
    /// are caught and logged — they MUST NOT block the pipeline; the ML
    /// classifier still runs on whatever text was assembled by other steps.
    member private this.TryEnrichWithForwardedPhotoOcr(msg: TgMessage) = task {
        if msg.ExternalReplyPhotoOcrApplied then ()
        elif botConfig.Value.ForwardSpamDetectionEnabled
             && botConfig.Value.OcrEnabled
             && isMessageFromAllowedChats botConfig.Value msg
             && not (isNull msg.ExternalReply)
             && not (isNull msg.ExternalReply.Photo)
             && msg.ExternalReply.Photo.Length > 0 then
            use activity = botActivity.StartActivity("forwardedPhotoOcrEnrichment")
            try
                let! ocrText = this.OcrPhotosFresh(msg.ExternalReply.Photo, msg.MessageId)
                match ocrText with
                | Some text ->
                    msg.PrependText(text)
                    %activity.SetTag("externalReplyOcrOutcome", "text")
                    %activity.SetTag("externalReplyOcrLength", text.Length)
                | None ->
                    %activity.SetTag("externalReplyOcrOutcome", "empty")
            with ex ->
                logger.LogError(ex, "Azure OCR failed for external-reply photo on message {MessageId}; continuing classification with text-only ML", msg.MessageId)
                %activity.SetTag("externalReplyOcrOutcome", "error")
            msg.ExternalReplyPhotoOcrApplied <- true
    }

    /// Azure-only OCR for the message's own photos. Same invariants as
    /// TryEnrichWithForwardedPhotoOcr — guarded on OwnPhotoOcrApplied,
    /// errors never block the pipeline.
    member private this.TryEnrichWithOcr(msg: TgMessage) = task {
        if msg.OwnPhotoOcrApplied then ()
        elif botConfig.Value.OcrEnabled
             && not (isNull msg.Photos) && msg.Photos.Length > 0
             && isMessageFromAllowedChats botConfig.Value msg then
            use activity = botActivity.StartActivity("ocrEnrichment")
            try
                let! ocrResult = this.OcrPhotosFresh(msg.Photos, msg.MessageId)
                match ocrResult with
                | Some ocrText ->
                    msg.AppendText(ocrText)
                    %activity.SetTag("ocrOutcome", "text")
                    %activity.SetTag("ocrTextLength", ocrText.Length)
                | None ->
                    %activity.SetTag("ocrOutcome", "empty")
            with ex ->
                logger.LogError(ex, "Azure OCR failed for message {MessageId}; continuing classification with text-only ML", msg.MessageId)
                %activity.SetTag("ocrOutcome", "error")
            msg.OwnPhotoOcrApplied <- true
    }

    member private _.TryEnrichWithInlineKeyboardText(msg: TgMessage) = task {
        if botConfig.Value.InlineKeyboardSpamDetectionEnabled
           && not (isNull msg.ReplyMarkup)
           && not (isNull msg.ReplyMarkup.InlineKeyboard)
           && isMessageFromAllowedChats botConfig.Value msg then
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

    // -----------------------------------------------------------------------
    // Private members — Callback handling
    // -----------------------------------------------------------------------

    member private this.VahterMarkedAsNotSpam(vahter: User, tgMsg: TgMessage) = task {
        let msgId = tgMsg.MessageId
        let chatId = tgMsg.ChatId
        let chatName = tgMsg.ChatUsername
        use _ =
            botActivity
                .StartActivity("vahterMarkedAsNotSpam")
                .SetTag("messageId", msgId)
                .SetTag("chatId", chatId)
        do! db.RecordMessageMarkedHam(chatId, msgId, (if isNull tgMsg.Text then "" else tgMsg.Text), None)

        let vahterUsername = vahter.Username |> Option.defaultValue null

        let logMsg = $"Vahter {prependUsername vahterUsername} ({vahter.Id}) marked message {msgId} in {prependUsername chatName}({chatId}) as false-positive (NOT A SPAM)\n{tgMsg.Text}"
        do! botClient.SendMessage(
                chatId = ChatId(botConfig.Value.AllLogsChannelId),
                text = logMsg
            ) |> taskIgnore
        logger.LogInformation logMsg
    }

    member private this.VahterMarkedAsSpam(vahter: User, tgMsg: TgMessage) = task {
        let msgId = tgMsg.MessageId
        let chatId = tgMsg.ChatId
        use _ =
            botActivity
                .StartActivity("vahterMarkedAsSpam")
                .SetTag("messageId", msgId)
                .SetTag("chatId", chatId)

        let isAuthed = isBanAuthorized botConfig.Value tgMsg vahter logger
        if isAuthed then
            let actor = Actor.User {| userId = vahter.Id; username = vahter.Username |}
            do! this.TotalBan(tgMsg, actor)
    }

    /// Soft spam handler - deletes message and marks as spam for ML, but does NOT ban user
    member private this.VahterSoftSpam(vahter: User, tgMsg: TgMessage) = task {
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
        do! db.RecordMessageMarkedSpam(chatId, msgId, None)

        // 3. Log the action
        let vahterUsername = vahter.Username |> Option.defaultValue null
        let logMsg = $"Vahter {prependUsername vahterUsername} ({vahter.Id}) marked message {msgId} in {prependUsername chatName}({chatId}) as SPAM (soft, no ban)\n{tgMsg.Text}"
        do! botClient.SendMessage(
                chatId = ChatId(botConfig.Value.AllLogsChannelId),
                text = logMsg
            ) |> taskIgnore
        logger.LogInformation logMsg

        // 4. Check auto-ban using shared logic (karma system)
        let actor = Actor.User {| userId = vahter.Id; username = vahter.Username |}
        let! _ = this.CheckAndAutoBan(tgMsg, actor)
        ()
    }

    // just an aux function to reduce indentation in onCallback and prevent FS3511
    member private this.OnCallbackAux(onCallbackActivity: Activity, vahter: User, callbackState: Callback, callbackData: CallbackMessage, callbackQuery: CallbackQuery) = task {
        // Route message-context callbacks (existing) vs reaction-context callbacks (new) separately —
        // they have different action keys for dedup (TryRecordVahterAction wants chatId+messageId for
        // a real message, but reaction-spam triage has no spam-message authored by the suspect).
        let answer (text: string) =
            botClient.AnswerCallbackQuery(callbackQuery.Id, text)
            |> safeTaskAwait (fun e -> logger.LogWarning($"Failed to answer callback query {callbackQuery.Id}", e))

        let cleanupActionMessage () = task {
            match callbackState.ActionMessageId with
            | Some msgId ->
                do! db.ExpireCallbacksByMessageId(msgId)
                do! botClient.DeleteMessage(ChatId callbackState.ActionChannelId, msgId)
                    |> safeTaskAwait (fun e -> logger.LogWarning ($"Failed to delete message {msgId} from action channel", e))
            | None -> ()
        }

        match callbackData with
        | NotASpam _ | Spam _ | MarkAsSpam _ ->
            let wrapper = match callbackData with NotASpam m | Spam m | MarkAsSpam m -> m | _ -> failwith "unreachable"
            let tgMsg = TgMessage.Create(wrapper.message)

            let actionType =
                match callbackData with
                | Spam _ -> PotentialKill
                | MarkAsSpam _ -> PotentialSoftSpam
                | NotASpam _ ->
                    if callbackState.ActionChannelId = botConfig.Value.DetectedSpamChannelId
                    then DetectedNotSpam
                    else PotentialNotSpam
                | ReactionBan _ | ReactionSpam _ | ReactionNotSpam _ -> failwith "unreachable"

            let! actionRecorded = db.TryRecordVahterAction(
                                    vahter.Id, actionType, tgMsg.SenderId,
                                    tgMsg.ChatId, tgMsg.MessageId)

            if actionRecorded then
                %onCallbackActivity.SetTag("actionRecorded", true)
                match callbackData with
                | NotASpam _ ->
                    %onCallbackActivity.SetTag("type", "NotASpam")
                    do! this.VahterMarkedAsNotSpam(vahter, tgMsg)
                | Spam _ ->
                    %onCallbackActivity.SetTag("type", "Spam")
                    do! this.VahterMarkedAsSpam(vahter, tgMsg)
                | MarkAsSpam _ ->
                    %onCallbackActivity.SetTag("type", "MarkAsSpam")
                    do! this.VahterSoftSpam(vahter, tgMsg)
                | ReactionBan _ | ReactionSpam _ | ReactionNotSpam _ -> ()

                do! answer "Done! +1 🎯"
            else
                %onCallbackActivity.SetTag("actionRecorded", false)
                logger.LogInformation $"Action already recorded for message {tgMsg.MessageId} in chat {tgMsg.ChatId}"
                do! answer "Already handled by another vahter"

        // Reaction-triage callbacks don't go through TryRecordVahterAction — that helper
        // dedups via the moderation:{chatId}:{messageId} stream, but a reaction-spam trip
        // has no specific spam message (messageId=0 collides across users in the same chat).
        // Click serialization is already provided upstream by ResolveCallback, so dedup here
        // is unnecessary.
        // Reaction-triage branches use CleanupReactionTriageCallbacksForUser instead of the
        // per-alert cleanupActionMessage(): the same suspect can trip the threshold in N chats
        // and end up with N alerts × 3 buttons. First click wins — all leftover alerts for the
        // same user get swept (including the current alert's two siblings).
        | ReactionBan ctx ->
            %onCallbackActivity.SetTag("type", "ReactionBan")
            let actor = Actor.User {| userId = vahter.Id; username = vahter.Username |}
            let! u = task {
                match! db.GetUserById(ctx.userId) with
                | Some u -> return u
                | None   -> return { User.Zero with Id = ctx.userId }
            }
            do! this.ReactionAct_Ban(ctx.chatId, 0, u, actor)
            do! answer "Banned 🚫"
            do! this.CleanupReactionTriageCallbacksForUser(ctx.userId)

        | ReactionSpam ctx ->
            %onCallbackActivity.SetTag("type", "ReactionSpam")
            let actor = Actor.User {| userId = vahter.Id; username = vahter.Username |}
            let! target = task {
                match! db.GetUserById(ctx.userId) with
                | Some u -> return u
                | None   -> return { User.Zero with Id = ctx.userId }
            }
            let! _ = this.ReactionAct_Spam(ctx.chatId, target, actor)
            do! answer "Reactions removed ⚠️"
            do! this.CleanupReactionTriageCallbacksForUser(ctx.userId)

        | ReactionNotSpam ctx ->
            %onCallbackActivity.SetTag("type", "ReactionNotSpam")
            let actor = Actor.User {| userId = vahter.Id; username = vahter.Username |}
            let! target = task {
                match! db.GetUserById(ctx.userId) with
                | Some u -> return u
                | None   -> return { User.Zero with Id = ctx.userId }
            }
            do! this.ReactionAct_NotSpam(target, actor)
            do! answer "Cooldown set ✅"
            do! this.CleanupReactionTriageCallbacksForUser(ctx.userId)

        // For non-reaction (message-context) callbacks, fall through to the same cleanup as before.
        match callbackData with
        | NotASpam _ | Spam _ | MarkAsSpam _ ->
            do! cleanupActionMessage()
        | ReactionBan _ | ReactionSpam _ | ReactionNotSpam _ -> ()
    }

    member private this.OnCallback(callbackQuery: CallbackQuery) = task {
        use onCallbackActivity = botActivity.StartActivity("onCallback")
        %onCallbackActivity.SetTag("callbackId", callbackQuery.Data)

        let callbackId = Guid.Parse callbackQuery.Data

        // Level 1: Atomically resolve callback (protection between button clicks)
        match! db.ResolveCallback(callbackId) with
        | None ->
            // Callback already processed by another vahter
            logger.LogInformation $"Callback {callbackId} already processed"
            do! botClient.AnswerCallbackQuery(callbackQuery.Id, "Already processed")
                |> safeTaskAwait (fun e -> logger.LogWarning($"Failed to answer callback query {callbackQuery.Id}", e))
        | Some callbackState ->
            let callbackData = deserializeCallbackData callbackState.Data.Value
            %onCallbackActivity.SetTag("callbackData", callbackData)

            match! db.GetUserById(callbackQuery.From.Id) with
            | None ->
                logger.LogWarning $"User {callbackQuery.From.Username} ({callbackQuery.From.Id}) tried to press callback button while not being in DB"
                do! botClient.AnswerCallbackQuery(callbackQuery.Id, "You are not in DB")
                    |> safeTaskAwait (fun e -> logger.LogWarning($"Failed to answer callback query {callbackQuery.Id}", e))
            | Some vahter ->
                %onCallbackActivity.SetTag("vahterUsername", vahter.Username)
                %onCallbackActivity.SetTag("vahterId", vahter.Id)

                // only vahters should be able to press message buttons
                let isAuthed = botConfig.Value.AllowedUsers.ContainsValue vahter.Id
                if not isAuthed then
                    logger.LogWarning $"User {callbackQuery.From.Username} ({callbackQuery.From.Id}) tried to press callback button while not being a certified vahter"
                    do! botClient.AnswerCallbackQuery(callbackQuery.Id, "Not authorized")
                        |> safeTaskAwait (fun e -> logger.LogWarning($"Failed to answer callback query {callbackQuery.Id}", e))
                else
                    do! this.OnCallbackAux(onCallbackActivity, vahter, callbackState, callbackData, callbackQuery)
    }

    // -----------------------------------------------------------------------
    // Private members — Reaction handling
    // -----------------------------------------------------------------------

    member private this.OnMessageReaction(reaction: MessageReactionUpdated) = task {
        use activity =
            botActivity
                .StartActivity("messageReaction")
                .SetTag("chatId", reaction.Chat.Id)
                .SetTag("chatUsername", reaction.Chat.Username)
                .SetTag("messageId", reaction.MessageId)
                .SetTag("userId", reaction.User.Id)
                .SetTag("userUsername", reaction.User.Username)

        // Check if reaction spam detection is enabled
        if not botConfig.Value.ReactionSpamEnabled then
            %activity.SetTag("skipped", "reactionSpamDisabled")
        // Check if chat is monitored
        elif not (botConfig.Value.ChatsToMonitor.ContainsValue reaction.Chat.Id) then
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
                // Extract the joined emoji string from NewReaction for the dossier display.
                // Premium custom emojis don't render as plain text — fall back to a tag.
                let emojiStr =
                    if isNull reaction.NewReaction then None
                    else
                        let parts =
                            reaction.NewReaction
                            |> Array.choose (fun r ->
                                match r with
                                | :? ReactionTypeEmoji as e       -> Option.ofObj e.Emoji
                                | :? ReactionTypeCustomEmoji as _ -> Some "[custom]"
                                | _                                -> None)
                        if parts.Length = 0 then None else Some (String.Concat parts)

                // Upsert user and increment reaction count atomically (records chatId/messageId
                // and the emoji so the vahter alert dossier can show what they reacted with)
                let! updatedUser =
                    db.UpsertUserAndIncrementReactions(reaction.User.Id, Option.ofObj reaction.User.Username, reaction.Chat.Id, reaction.MessageId, emojiStr, added)

                %activity.SetTag("totalReactionCount", updatedUser.ReactionCount)

                // Cooldown short-circuit: a previous LLM/vahter NOT_SPAM verdict means we trust this
                // user is a legit lurker — don't re-trigger the pipeline (no LLM call, no admin alert).
                if updatedUser.IsInReactionTriageCooldown(utcNow()) then
                    %activity.SetTag("action", "cooldown")
                else
                    // Check heuristics: if user has few messages but many reactions -> trip the pipeline.
                    let! msgCount =
                        db.CountUniqueUserMsgsUpTo(updatedUser.Id, botConfig.Value.ReactionSpamMinMessages + 1)
                    %activity.SetTag("messageCount", msgCount)

                    if msgCount < botConfig.Value.ReactionSpamMinMessages &&
                       updatedUser.ReactionCount >= botConfig.Value.ReactionSpamMaxReactions then
                        logger.LogWarning(
                            "Reaction spam threshold tripped: {Username} ({UserId}) has {MsgCount} messages but {ReactionCount} reactions",
                            reaction.User.Username,
                            reaction.User.Id,
                            msgCount,
                            updatedUser.ReactionCount
                        )
                        %activity.SetTag("action", "triage")
                        do! this.RunReactionTriagePipeline(reaction, updatedUser)
                    else
                        %activity.SetTag("action", "none")
    }

    // -----------------------------------------------------------------------
    // Public entry point
    // -----------------------------------------------------------------------

    member this.OnUpdate(update: Update) = task {
        use _ = botActivity.StartActivity("onUpdate")
        if update.CallbackQuery <> null then
            do! this.OnCallback(update.CallbackQuery)
        elif update.MessageReaction <> null then
            do! this.OnMessageReaction(update.MessageReaction)
        elif update.EditedOrMessage <> null then
            let isEdit = update.EditedMessage <> null
            let msg = TgMessage.Create(update.EditedOrMessage, isEdit = isEdit)
            this.TryEnrichWithForwardedQuoteText(msg)
            do! this.TryEnrichWithInlineKeyboardText(msg)
            do! this.TryEnrichOcrFromCache(msg)
            do! this.OnMessage(msg)
        elif update.ChatMember <> null || update.MyChatMember <> null then
            // expected update type, nothing to do
            ()
        else
            // unknown update type, just log and ignore
            logger.LogWarning("Unknown update type: {UpdateType}", update.Type)
    }
