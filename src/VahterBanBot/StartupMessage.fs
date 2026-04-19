module VahterBanBot.StartupMessage

open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open VahterBanBot.Types
open VahterBanBot.Utils
open BotInfra
open Microsoft.Extensions.Hosting

type StartupMessage(
    logger: ILogger<StartupMessage>,
    botConf: IOptions<BotConfiguration>
) =
    let getStartLogMsg() =
        let cfg = botConf.Value
        let sb = StringBuilder()
        %sb.AppendLine("Bot started with following configuration")
        %sb.AppendLine("AllowedUsers:")
        for KeyValue(username, userId) in cfg.AllowedUsers do
            %sb.AppendLine($"  {prependUsername username} ({userId})")
        %sb.AppendLine("ChatsToMonitor:")
        for KeyValue(username, chatId) in cfg.ChatsToMonitor do
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
