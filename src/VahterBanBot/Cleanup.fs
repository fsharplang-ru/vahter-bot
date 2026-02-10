module VahterBanBot.Cleanup

open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open VahterBanBot.ML
open VahterBanBot.Types
open VahterBanBot.Utils
open System
open System.Threading
open Microsoft.Extensions.Hosting

type CleanupService(
    logger: ILogger<CleanupService>,
    telegramClient: ITelegramBotClient,
    botConf: BotConfiguration,
    ml: MachineLearning
) =
    let podId = getEnvOr "POD_NAME" Environment.MachineName
    let mutable cts: CancellationTokenSource = null
    let mutable backgroundTask: Task = null
    
    /// Cleanup job - runs at configured hour UTC
    /// Cleans up old messages, callbacks, and detected spam from channel
    let runCleanup () = task {
        let sb = StringBuilder()
        
        if botConf.CleanupOldMessages then
            let! cleanupMsgs = DB.cleanupOldMessages botConf.CleanupOldLimit
            %sb.AppendLine $"Cleaned up {cleanupMsgs} messages from DB which are older than {timeSpanAsHumanReadable botConf.CleanupOldLimit}"
        
        // Cleanup failed posts (callbacks without message_id, older than 5 minutes)
        let! failedPosts = DB.getCallbacksWithoutMessageId (TimeSpan.FromMinutes 5L)
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
                    do! telegramClient.DeleteMessage(
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

        let msg = sb.ToString()
        if msg.Length > 0 then
            do! telegramClient.SendMessage(
                    chatId = ChatId(botConf.AllLogsChannelId),
                    text = msg
                ) |> taskIgnore
            logger.LogInformation msg
    }
    
    /// Stats job - runs at configured hour UTC
    /// Sends daily vahter statistics to Telegram
    let runStats () = task {
        let sb = StringBuilder()
        
        // Vahter action stats (new system)
        let! actionStats = DB.getVahterActionStats (Some botConf.CleanupInterval)
        %sb.AppendLine(string actionStats)

        let msg = sb.ToString()
        do! telegramClient.SendMessage(
                chatId = ChatId(botConf.AllLogsChannelId),
                text = msg
            ) |> taskIgnore
        logger.LogInformation msg
    }
    
    let tryRunJob (jobName: string) (scheduledTime: TimeSpan) (jobAction: unit -> Task<unit>) = task {
        let! acquired = DB.tryAcquireScheduledJob jobName scheduledTime podId
        if acquired then
            logger.LogInformation("Acquired {JobName} job (pod: {PodId})", jobName, podId)
            try
                do! jobAction()
                do! DB.completeScheduledJob jobName
                logger.LogInformation("{JobName} completed successfully (pod: {PodId})", jobName, podId)
            with ex ->
                // Job failed but lease will expire in 1 hour, allowing retry
                logger.LogError(ex, "{JobName} failed (pod: {PodId}), will retry after lease expires", jobName, podId)
    }
    
    let runSchedulerLoop (ct: CancellationToken) = task {
        logger.LogInformation("Scheduler service started (pod: {PodId}, check interval: {Interval})", podId, botConf.CleanupCheckInterval)
        
        // Use PeriodicTimer for async-friendly scheduling
        use timer = new PeriodicTimer(botConf.CleanupCheckInterval)
        
        // Initial check after a short delay (don't run immediately on startup)
        let! _ = Task.Delay(TimeSpan.FromSeconds 30L, ct)
        
        while not ct.IsCancellationRequested do
            try
                // Check scheduled jobs
                do! tryRunJob "daily_cleanup" (TimeSpan.FromHours botConf.CleanupScheduledHour) runCleanup
                do! tryRunJob "daily_stats" (TimeSpan.FromHours botConf.StatsScheduledHour) runStats

                if botConf.MlEnabled then
                    do! tryRunJob "daily_ml_retrain" botConf.MlRetrainScheduledTime (fun () -> ml.RetrainAndSave())

                // Check if another pod retrained a newer model (all pods)
                do! ml.TryReloadIfNewer()
            with 
            | :? OperationCanceledException -> ()
            | ex ->
                logger.LogError(ex, "Error in scheduler loop (pod: {PodId})", podId)
            
            try
                let! _ = timer.WaitForNextTickAsync(ct)
                ()
            with :? OperationCanceledException -> ()
    }

    interface IHostedService with
        member this.StartAsync _ =
            if not botConf.IgnoreSideEffects then
                cts <- new CancellationTokenSource()
                backgroundTask <- Task.Factory.StartNew((fun () -> runSchedulerLoop cts.Token), TaskCreationOptions.LongRunning).Unwrap()
            Task.CompletedTask

        member this.StopAsync _ = task {
            if not (isNull cts) then
                cts.Cancel()
                try
                    // Wait for the background task to complete gracefully
                    do! Task.WhenAny(backgroundTask, Task.Delay(TimeSpan.FromSeconds 5L)) |> taskIgnore
                with _ -> ()
                cts.Dispose()
        }
