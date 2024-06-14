module VahterBanBot.StartupMessage

open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open VahterBanBot.Types
open VahterBanBot.Utils
open Microsoft.Extensions.Hosting

type StartupMessage(
    logger: ILogger<StartupMessage>,
    telegramClient: ITelegramBotClient,
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

        let totalStats = (DB.getVahterStats None).Result
        %sb.AppendLine (string totalStats)

        sb.ToString()
    interface IHostedService with
        member this.StartAsync _ = task {
            if not botConf.IgnoreSideEffects then
                let startLogMsg = getStartLogMsg()
                logger.LogInformation startLogMsg
                do! telegramClient.SendTextMessageAsync(ChatId(botConf.LogsChannelId), startLogMsg)
                    |> taskIgnore
        }

        member this.StopAsync _ =
            Task.CompletedTask
