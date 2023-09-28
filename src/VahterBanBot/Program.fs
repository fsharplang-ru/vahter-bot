open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Microsoft.FSharp.Core
open Newtonsoft.Json
open Telegram.Bot
open Telegram.Bot.Types
open Giraffe
open Microsoft.Extensions.DependencyInjection
open VahterBanBot
open VahterBanBot.Utils
open VahterBanBot.Bot
open VahterBanBot.Types

type Root = class end

Dapper.FSharp.PostgreSQL.OptionTypes.register()

let botConf =
    { BotToken = getEnv "BOT_TELEGRAM_TOKEN"
      Route = getEnvOr "BOT_HOOK_ROUTE" "/bot"
      SecretToken = getEnv "BOT_AUTH_TOKEN"
      LogsChannelId = getEnv "LOGS_CHANNEL_ID" |> int64
      ChatsToMonitor = getEnv "CHATS_TO_MONITOR" |> JsonConvert.DeserializeObject<_>
      AllowedUsers = getEnv "ALLOWED_USERS" |> JsonConvert.DeserializeObject<_>
      ShouldDeleteChannelMessages = getEnvOr "SHOULD_DELETE_CHANNEL_MESSAGES" "true" |> bool.Parse }

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

%builder.Logging.AddConsole()

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
    
let webApp = choose [
    // need for Azure health checks on / route
    GET >=> route "/" >=> text "OK"
    
    requiresApiKey >=> choose [
        POST >=> route botConf.Route >=> bindJson<Update> (fun update next ctx -> task {
            use scope = ctx.RequestServices.CreateScope()
            let telegramClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>()
            let logger = ctx.GetLogger<Root>()
            try
                do! onUpdate telegramClient botConf (ctx.GetLogger "VahterBanBot.Bot") update.Message
            with e ->
                logger.LogError(e, "Unexpected error while processing update")
            return! Successful.OK() next ctx
        })
    ]
]

let app = builder.Build()

app.UseGiraffe(webApp)
let server = app.RunAsync()

let telegramClient = app.Services.GetRequiredService<ITelegramBotClient>()

let startLogMsg =
    let prependUsername (s: string) =
        if s.StartsWith("@") then s else "@" + s
    let sb = System.Text.StringBuilder()
    %sb.AppendLine("Bot started with following configuration")
    %sb.AppendLine("AllowedUsers:")
    for KeyValue(username, userId) in botConf.AllowedUsers do
        %sb.AppendLine($"  {prependUsername username} ({userId})")
    %sb.AppendLine("ChatsToMonitor:")
    for KeyValue(username, chatId) in botConf.ChatsToMonitor do
        %sb.AppendLine($"  {prependUsername username} ({chatId})")
    sb.ToString()

app.Logger.LogInformation startLogMsg
telegramClient.SendTextMessageAsync(ChatId(botConf.LogsChannelId), startLogMsg).Wait()

server.Wait()
