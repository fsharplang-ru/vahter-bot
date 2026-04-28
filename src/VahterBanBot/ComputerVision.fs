module VahterBanBot.ComputerVision

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open VahterBanBot.Types
open BotInfra
open Telegram.Bot

[<AllowNullLiteral>]
type IComputerVision =
    abstract member TextFromImageUrl: url: string -> Task<string>

/// Adapter that delegates to the shared IBotOcr (bytes-based) by downloading the file first.
type BotOcrComputerVision(botOcr: IBotOcr, botClient: ITelegramBotClient, logger: ILogger<BotOcrComputerVision>) =
    interface IComputerVision with
        member _.TextFromImageUrl(url: string) = task {
            if isNull botOcr then
                return null
            else
                try
                    // Extract file path from Telegram URL and download via bot client
                    // URL format: {apiBase}/file/bot{token}/{filePath}
                    let filePathStart = url.IndexOf("/file/bot")
                    if filePathStart < 0 then
                        logger.LogWarning("Unexpected file URL format: {Url}", url)
                        return null
                    else
                        // After /file/bot{token}/ is the file path
                        let afterBot = url.Substring(filePathStart + "/file/bot".Length)
                        let slashIdx = afterBot.IndexOf('/')
                        if slashIdx < 0 then
                            logger.LogWarning("Cannot extract file path from URL: {Url}", url)
                            return null
                        else
                            let filePath = afterBot.Substring(slashIdx + 1)
                            use ms = new MemoryStream()
                            do! botClient.DownloadFile(filePath, ms)
                            let bytes = ReadOnlyMemory<byte>(ms.ToArray())
                            return! botOcr.TextFromImageBytes(bytes)
                with ex ->
                    logger.LogError(ex, "Failed to download and OCR file from {Url}", url)
                    return null
        }
