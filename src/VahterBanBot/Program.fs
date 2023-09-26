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
open VahterBanBot.Utils
open VahterBanBot.Bot
open VahterBanBot.Types

type Root = class end

let botConf =
    { BotToken = getEnv "BOT_TELEGRAM_TOKEN"
      Route = getEnvOr "BOT_HOOK_ROUTE" "/bot"
      SecretToken = getEnv "BOT_AUTH_TOKEN"
      LogsChannelId = getEnv "LOGS_CHANNEL_ID" |> int64
      ChatsToMonitor = getEnv "CHATS_TO_MONITOR" |> JsonConvert.DeserializeObject<int64[]> |> Set.ofArray
      AllowedUsers = getEnv "ALLOWED_USERS" |> JsonConvert.DeserializeObject<int64[]> |> Set.ofArray
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
    POST >=> route botConf.Route >=> requiresApiKey >=> bindJson<Update> (fun update next ctx -> task {
        use scope = ctx.RequestServices.CreateScope()
        let telegramClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>()
        let logger = ctx.GetLogger<Root>()
        try
            do! onUpdate telegramClient botConf (ctx.GetLogger "VahterBanBot.Bot") update.Message
        with e ->
            logger.LogError(e, "Unexpected error while processing update")
        return! Successful.OK() next ctx
    })
    // need for Azure health checks on / route
    GET >=> route "/" >=> text "OK"
]

let app = builder.Build()

app.UseGiraffe(webApp)
let server = app.RunAsync()

let telegramClient = app.Services.GetRequiredService<ITelegramBotClient>()
telegramClient.SendTextMessageAsync(ChatId(botConf.LogsChannelId), "Bot started").Wait()

app.Logger.LogInformation("Bot started")
server.Wait()
