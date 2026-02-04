module VahterBanBot.StartupMessage

open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open VahterBanBot.Types
open VahterBanBot.Utils
open Microsoft.Extensions.Hosting

type StartupMessage(
    logger: ILogger<StartupMessage>,
    botConf: BotConfiguration
) =
    let getStartLogMsg() =
        let sb = StringBuilder()
        %sb.AppendLine("Bot started with following configuration")
        %sb.AppendLine("AllowedUsers:")
        for KeyValue(username, userId) in botConf.AllowedUsers do
            %sb.AppendLine($"  {prependUsername username} ({userId})")
        %sb.AppendLine("ChatsToMonitor:")
        for KeyValue(username, chatId) in botConf.ChatsToMonitor do
            %sb.AppendLine($"  {prependUsername username} ({chatId})")

        sb.ToString()

    interface IHostedService with
        member this.StartAsync _ =
            // Only log locally - Telegram notifications are sent via daily cleanup job
            let startLogMsg = getStartLogMsg()
            logger.LogInformation startLogMsg
            Task.CompletedTask

        member this.StopAsync _ =
            Task.CompletedTask
