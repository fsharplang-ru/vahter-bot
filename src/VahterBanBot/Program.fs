open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Microsoft.FSharp.Core
open Newtonsoft.Json
open Telegram.Bot
open Telegram.Bot.Polling
open Telegram.Bot.Types
open Giraffe
open Microsoft.Extensions.DependencyInjection
open Telegram.Bot.Types.Enums
open VahterBanBot
open VahterBanBot.Cleanup
open VahterBanBot.Utils
open VahterBanBot.Bot
open VahterBanBot.Types
open OpenTelemetry.Trace
open OpenTelemetry.Metrics
open OpenTelemetry.Resources
open Npgsql
open Azure.Monitor.OpenTelemetry.AspNetCore

type Root = class end

Dapper.FSharp.PostgreSQL.OptionTypes.register()

let botConf =
    { BotToken = getEnv "BOT_TELEGRAM_TOKEN"
      Route = getEnvOr "BOT_HOOK_ROUTE" "/bot"
      SecretToken = getEnv "BOT_AUTH_TOKEN"
      LogsChannelId = getEnv "LOGS_CHANNEL_ID" |> int64
      ChatsToMonitor = getEnv "CHATS_TO_MONITOR" |> JsonConvert.DeserializeObject<_>
      AllowedUsers = getEnv "ALLOWED_USERS" |> JsonConvert.DeserializeObject<_>
      ShouldDeleteChannelMessages = getEnvOr "SHOULD_DELETE_CHANNEL_MESSAGES" "true" |> bool.Parse
      IgnoreSideEffects = getEnvOr "IGNORE_SIDE_EFFECTS" "false" |> bool.Parse
      UsePolling =  getEnvOr "USE_POLLING" "false" |> bool.Parse }

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
    .AddHostedService<CleanupService>()
    .AddHttpClient("telegram_bot_client")
    .AddTypedClient(fun httpClient sp ->
        let options = TelegramBotClientOptions(botConf.BotToken)
        TelegramBotClient(options, httpClient) :> ITelegramBotClient)

let otelBuilder =
    builder.Services
        .AddOpenTelemetry()
        .WithTracing(fun builder ->
            %builder
                .AddHttpClientInstrumentation()
                .AddAspNetCoreInstrumentation()
                .AddNpgsql()
                .ConfigureResource(fun res ->
                    %res.AddAttributes [
                        KeyValuePair("service.name", "vahter-ban-bot")
                    ]
                )
                .AddSource(botActivity.Name)
            getEnvWith "OTEL_EXPORTER_ZIPKIN_ENDPOINT" (fun _ ->
                %builder.AddZipkinExporter()
            )
            getEnvWith "OTEL_EXPORTER_CONSOLE"  (bool.Parse >> fun otelConsole ->
                if otelConsole then %builder.AddConsoleExporter()
            )
        )
        .WithMetrics(fun builder ->
            %builder
                .AddHttpClientInstrumentation()
                .AddAspNetCoreInstrumentation()
            getEnvWith "OTEL_EXPORTER_CONSOLE"  (bool.Parse >> fun otelConsole ->
                if otelConsole then %builder.AddConsoleExporter()
            )
        )

getEnvWith "APPLICATIONINSIGHTS_CONNECTION_STRING" (fun appInsightKey ->
    %otelBuilder.UseAzureMonitor(fun options ->
        options.ConnectionString <- appInsightKey
    )
    %builder.Logging.AddApplicationInsights(
        configureTelemetryConfiguration = (fun config ->
            config.ConnectionString <- appInsightKey
        ),
        configureApplicationInsightsLoggerOptions = (fun config ->
            ()
        )
    )
)

%builder.Logging.AddConsole()
    
let webApp = choose [
    // need for Azure health checks on any route
    GET >=> text "OK"

    POST >=> route botConf.Route >=> requiresApiKey >=> bindJson<Update> (fun update next ctx -> task {
        let updateBodyJson =
            try JsonConvert.SerializeObject update
            with e -> e.Message
        use banOnReplyActivity =
            botActivity
              .StartActivity("postUpdate")
              .SetTag("updateBodyObject", update)
              .SetTag("updateBodyJson", updateBodyJson)
        
        banOnReplyActivity.SetCustomProperty("updateBodyObject", update)
        banOnReplyActivity.SetCustomProperty("updateBodyJson", updateBodyJson)
        
        use scope = ctx.RequestServices.CreateScope()
        let telegramClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>()
        let logger = ctx.GetLogger<Root>()
        try
            do! onUpdate telegramClient botConf (ctx.GetLogger "VahterBanBot.Bot") update.Message
        with e ->
            logger.LogError(e, $"Unexpected error while processing update: {updateBodyJson}")

        return! Successful.OK() next ctx
    })
]

let app = builder.Build()

app.UseGiraffe(webApp)
let server = app.RunAsync()

let telegramClient = app.Services.GetRequiredService<ITelegramBotClient>()

let getStartLogMsg() =
    let sb = System.Text.StringBuilder()
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

if not botConf.IgnoreSideEffects then
    let startLogMsg = getStartLogMsg()
    app.Logger.LogInformation startLogMsg
    telegramClient.SendTextMessageAsync(ChatId(botConf.LogsChannelId), startLogMsg).Wait()

// Dev mode only
if botConf.UsePolling then
    let pollingHandler = {
        new IUpdateHandler with
          member x.HandleUpdateAsync (botClient: ITelegramBotClient, update: Update, cancellationToken: CancellationToken) =
            task {
                if update.Message <> null && update.Message.Type = MessageType.Text then
                    let ctx = app.Services.CreateScope()
                    let logger = ctx.ServiceProvider.GetRequiredService<ILogger<IUpdateHandler>>()
                    let client = ctx.ServiceProvider.GetRequiredService<ITelegramBotClient>()
                    do! onUpdate client botConf logger update.Message
            }
          member x.HandlePollingErrorAsync (botClient: ITelegramBotClient, ex: Exception, cancellationToken: CancellationToken) =
              Task.CompletedTask
    }
    telegramClient.StartReceiving(pollingHandler, null, CancellationToken.None)

server.Wait()
