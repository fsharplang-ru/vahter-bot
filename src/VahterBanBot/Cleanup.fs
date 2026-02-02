module VahterBanBot.Cleanup

open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open VahterBanBot.Types
open VahterBanBot.Utils
open System
open System.Threading
open Microsoft.Extensions.Hosting

type CleanupService(
    logger: ILogger<CleanupService>,
    telegramClient: ITelegramBotClient,
    botConf: BotConfiguration
) =
    let mutable timer: Timer = null
    
    let cleanup _ = task {
        let sb = StringBuilder()
        
        if botConf.CleanupOldMessages then
            let! cleanupMsgs = DB.cleanupOldMessages botConf.CleanupOldLimit
            %sb.AppendLine $"Cleaned up {cleanupMsgs} messages from DB which are older than {timeSpanAsHumanReadable botConf.CleanupOldLimit}"
        
        // Cleanup failed posts (callbacks without message_id, older than 5 minutes)
        let! failedPosts = DB.getCallbacksWithoutMessageId (TimeSpan.FromMinutes 5)
        for callback in failedPosts do
            do! DB.deleteCallback callback.id
        if failedPosts.Length > 0 then
            %sb.AppendLine $"Cleaned up {failedPosts.Length} failed callback posts"
        
        // Cleanup old Detected Spam messages from detected spam channel
        let! oldDetectedSpam = DB.getOldDetectedSpamCallbacks botConf.DetectedSpamCleanupAge botConf.DetectedSpamChannelId
        let mutable deletedFromChannel = 0
        for callback in oldDetectedSpam do
            match callback.action_message_id with
            | Some msgId ->
                try
                    do! telegramClient.DeleteMessageAsync(
                        ChatId(botConf.DetectedSpamChannelId),
                        msgId
                    )
                    deletedFromChannel <- deletedFromChannel + 1
                with ex ->
                    logger.LogWarning(ex, $"Failed to delete message {msgId} from Detected Spam channel")
            | None -> ()
            do! DB.deleteCallback callback.id
        if oldDetectedSpam.Length > 0 then
            %sb.AppendLine $"Cleaned up {oldDetectedSpam.Length} old detected spam callbacks ({deletedFromChannel} messages deleted from channel)"
        
        // Cleanup very old callbacks (fallback, older than CleanupOldLimit)
        let! cleanupCallbacks = DB.cleanupOldCallbacks botConf.CleanupOldLimit
        if cleanupCallbacks > 0 then
            %sb.AppendLine $"Cleaned up {cleanupCallbacks} very old callbacks from DB"

        // Vahter ban stats (old system)
        let! vahterStats = DB.getVahterStats (Some botConf.CleanupInterval)
        %sb.AppendLine(string vahterStats)
        
        // Vahter action stats (new system)
        let! actionStats = DB.getVahterActionStats (Some botConf.CleanupInterval)
        %sb.AppendLine(string actionStats)

        let msg = sb.ToString()
        do! telegramClient.SendTextMessageAsync(
                chatId = ChatId(botConf.AllLogsChannelId),
                text = msg
            ) |> taskIgnore
        logger.LogInformation msg
    }

    interface IHostedService with
        member this.StartAsync _ =
            if not botConf.IgnoreSideEffects then
                timer <- new Timer(TimerCallback(cleanup >> ignore), null, TimeSpan.Zero, botConf.CleanupInterval)
            Task.CompletedTask

        member this.StopAsync _ =
            match timer with
            | null -> Task.CompletedTask
            | timer -> timer.DisposeAsync().AsTask()
