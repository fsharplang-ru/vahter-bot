module VahterBanBot.StartupMessage

open System.Text
open System.Threading.Tasks
open Funogram.Telegram.Types
open Microsoft.Extensions.Logging
open VahterBanBot.Types
open VahterBanBot.Utils
open Microsoft.Extensions.Hosting

type StartupMessage(
    logger: ILogger<StartupMessage>,
    telegramClient: TelegramBotClient,
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
        member this.StartAsync _ = task {
            if not botConf.IgnoreSideEffects then
                let startLogMsg = getStartLogMsg()
                logger.LogInformation startLogMsg
                do! telegramClient.SendTextMessageAsync(ChatId.Int(botConf.LogsChannelId), startLogMsg)
                    |> taskIgnore
        }

        member this.StopAsync _ =
            Task.CompletedTask
