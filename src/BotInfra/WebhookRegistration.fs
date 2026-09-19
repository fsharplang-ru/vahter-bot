namespace BotInfra

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging

/// Opt-in Telegram webhook self-registration — a bot calls `registerAsync`,
/// typically fire-and-forget from a startup hosted service (see Fizruk).
module WebhookRegistration =

    let private allowedUpdates = [| "message" |]

    /// Recurses (not nested in another task CE — required for a statically
    /// compilable state machine) until setWebhook succeeds or attempts run out.
    let rec private attemptSetWebhook
        (tg: ITelegramApi)
        req
        (webhookUrl: string)
        (maxAttempts: int)
        (retryDelay: TimeSpan)
        (logger: ILogger)
        (n: int)
        : Task =
        task {
            try
                match! tg.Call req with
                | Ok _ -> logger.LogInformation("Webhook registered: {Url}", webhookUrl)
                | Error e ->
                    logger.LogWarning(
                        "setWebhook failed (attempt {Attempt}/{Max}): {Code} {Description}",
                        n, maxAttempts, e.ErrorCode, e.Description)
                    if n < maxAttempts then
                        do! Task.Delay retryDelay
                        do! attemptSetWebhook tg req webhookUrl maxAttempts retryDelay logger (n + 1)
                    else
                        logger.LogError("setWebhook giving up after {Max} attempts", maxAttempts)
            with ex ->
                logger.LogWarning(ex, "setWebhook threw (attempt {Attempt}/{Max})", n, maxAttempts)
                if n < maxAttempts then
                    do! Task.Delay retryDelay
                    do! attemptSetWebhook tg req webhookUrl maxAttempts retryDelay logger (n + 1)
                else
                    logger.LogError(ex, "setWebhook giving up after {Max} attempts", maxAttempts)
        }
        :> Task

    /// getWebhookInfo can't reveal the secret token, so setWebhook always runs
    /// (idempotent) when `webhookUrl` is set; getWebhookInfo only logs the before-state.
    let registerAsync
        (tg: ITelegramApi)
        (webhookUrl: string)
        (secretToken: string)
        (maxAttempts: int)
        (retryDelay: TimeSpan)
        (logger: ILogger)
        : Task =
        task {
            if String.IsNullOrWhiteSpace webhookUrl then
                logger.LogInformation "webhook self-registration disabled"
            else
                try
                    match! tg.Call(Funogram.Telegram.Req.GetWebhookInfo.Make()) with
                    | Ok info ->
                        logger.LogInformation(
                            "Webhook before self-registration: url={Url} pendingUpdateCount={Pending}",
                            info.Url, info.PendingUpdateCount)
                    | Error e ->
                        logger.LogWarning(
                            "getWebhookInfo failed (logging before-state only): {Code} {Description}",
                            e.ErrorCode, e.Description)
                with ex ->
                    logger.LogWarning(ex, "getWebhookInfo threw (logging before-state only)")

                let req =
                    Funogram.Telegram.Req.SetWebhook.Make(
                        webhookUrl,
                        allowedUpdates = allowedUpdates,
                        dropPendingUpdates = false,
                        secretToken = secretToken)

                do! attemptSetWebhook tg req webhookUrl maxAttempts retryDelay logger 1
        }
        :> Task
