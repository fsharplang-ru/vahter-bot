module VahterBanBot.Cleanup

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
        
        let msg = $"Cleaned up {cleanupMsgs} messages"
        
        let! _ = telegramClient.SendTextMessageAsync(
            ChatId(botConf.LogsChannelId),
            msg
        )
        logger.LogInformation msg
    }
    
    interface IHostedService with
        member this.StartAsync(cancellationToken) =
            timer <- new Timer(TimerCallback(cleanup >> ignore), null, TimeSpan.Zero, cleanupInterval)
            Task.CompletedTask

        member this.StopAsync(cancellationToken) =
            match timer with
            | null -> Task.CompletedTask
            | timer -> timer.DisposeAsync().AsTask()
