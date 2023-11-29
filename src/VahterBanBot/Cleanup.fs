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
    let cleanupInterval =
        getEnvOr "MESSAGES_CLEANUP_INTERVAL_SEC" "86400" // 1 day
        |> int
        |> TimeSpan.FromSeconds
    let cleanupOldLimit =
        getEnvOr "MESSAGES_CLEANUP_OLD_LIMIT_SEC" "259200" // 3 days
        |> int
        |> TimeSpan.FromSeconds
    let mutable timer: Timer = null
    
    let cleanup _ = task {
        let! cleanupMsgs = DB.cleanupOldMessages cleanupOldLimit
        let! vahterStats = DB.getVahterStats (Some cleanupInterval)
        
        let sb = StringBuilder()
        %sb.AppendLine $"Cleaned up {cleanupMsgs} messages from DB which are older than {timeSpanAsHumanReadable cleanupOldLimit}"
        %sb.AppendLine(string vahterStats)
        
        let msg = sb.ToString()
        do! telegramClient.SendTextMessageAsync(
                ChatId(botConf.LogsChannelId),
                msg
            ) |> taskIgnore
        logger.LogInformation msg
    }
    
    interface IHostedService with
        member this.StartAsync _ =
            if not botConf.IgnoreSideEffects then
                timer <- new Timer(TimerCallback(cleanup >> ignore), null, TimeSpan.Zero, cleanupInterval)
            Task.CompletedTask

        member this.StopAsync _ =
            match timer with
            | null -> Task.CompletedTask
            | timer -> timer.DisposeAsync().AsTask()
