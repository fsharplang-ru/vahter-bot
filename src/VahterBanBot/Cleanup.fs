module VahterBanBot.Cleanup

open System.Text
open System.Threading.Tasks
open Funogram.Telegram.Types
open Microsoft.Extensions.Logging
open VahterBanBot.Types
open VahterBanBot.Utils
open System
open System.Threading
open Microsoft.Extensions.Hosting

type CleanupService(
    logger: ILogger<CleanupService>,
    telegramClient: TelegramBotClient,
    botConf: BotConfiguration
) =
    let mutable timer: Timer = null
    
    let cleanup _ = task {
        let sb = StringBuilder()
        if botConf.CleanupOldMessages then
            let! cleanupMsgs = DB.cleanupOldMessages botConf.CleanupOldLimit
            %sb.AppendLine $"Cleaned up {cleanupMsgs} messages from DB which are older than {timeSpanAsHumanReadable botConf.CleanupOldLimit}"
            let! cleanupCallbacks = DB.cleanupOldCallbacks botConf.CleanupOldLimit
            %sb.AppendLine $"Cleaned up {cleanupCallbacks} callbacks from DB which are older than {timeSpanAsHumanReadable botConf.CleanupOldLimit}"

        let! vahterStats = DB.getVahterStats (Some botConf.CleanupInterval)
        %sb.AppendLine(string vahterStats)

        let msg = sb.ToString()
        do! telegramClient.SendTextMessageAsync(
                ChatId.Int(botConf.LogsChannelId),
                msg
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
