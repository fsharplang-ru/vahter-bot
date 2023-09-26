open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open Giraffe
open Microsoft.Extensions.DependencyInjection
open VahterBanBot.Utils

[<CLIMutable>]
type BotConfiguration =
    { BotToken: string
      Route: string
      SecretToken: string
      LogsChannelId: int64 }

let botConf =
    { BotToken = getEnv "BOT_TELEGRAM_TOKEN"
      Route = getEnvOr "BOT_HOOK_ROUTE" "/bot"
      SecretToken = getEnv "BOT_AUTH_TOKEN"
      LogsChannelId = getEnv "LOGS_CHANNEL_ID" |> int64 }

let validateApiKey (ctx : HttpContext) =
    match ctx.TryGetRequestHeader "X-Telegram-Bot-Api-Secret-Token" with
    | Some key when key = botConf.SecretToken -> true
    | _ -> false

let accessDenied = setStatusCode 401 >=> text "Access Denied"
let requiresApiKey = authorizeRequest validateApiKey accessDenied

let builder = WebApplication.CreateBuilder()
%builder.Services
    .AddSingleton(botConf)
    .AddGiraffe()
    .AddHttpClient("telegram_bot_client")
    .AddTypedClient(fun httpClient sp ->
        let options = TelegramBotClientOptions(botConf.BotToken)
        TelegramBotClient(options, httpClient) :> ITelegramBotClient)

match Environment.GetEnvironmentVariable "APPLICATIONINSIGHTS_CONNECTION_STRING" with
| null -> ()
| appInsightKey ->
    %builder.Logging.AddApplicationInsights(
        configureTelemetryConfiguration = (fun config ->
            config.ConnectionString <- appInsightKey
        ),
        configureApplicationInsightsLoggerOptions = (fun config ->
            ()
        )
    )

let app = builder.Build()

let webApp = choose [
    POST >=> route botConf.Route >=> requiresApiKey >=> bindJson<Update> (fun update next ctx -> task {
        use scope = ctx.RequestServices.CreateScope()
        let telegramClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();
        
        if update.Message <> null then
            let! _ = telegramClient.SendTextMessageAsync(ChatId(update.Message.Chat.Id), $"Received {update.Message.Text}") in ()
        
        return! Successful.OK() next ctx
    })
    GET >=> route "/" >=> text "Hello World! Check CD"
]

app.UseGiraffe(webApp)
let server = app.RunAsync()

let telegramClient = builder.Services.BuildServiceProvider().GetRequiredService<ITelegramBotClient>()
telegramClient.SendTextMessageAsync(ChatId(botConf.LogsChannelId), "Bot started").Wait()

let topLogger = builder.Services.BuildServiceProvider().GetRequiredService<ILogger>()
topLogger.LogInformation("Bot started")

server.Wait()
