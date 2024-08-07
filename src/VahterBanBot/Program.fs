﻿#nowarn "44" // open telemetry is going crazy with warnings

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Dapper
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
open VahterBanBot.Cleanup
open VahterBanBot.ML
open VahterBanBot.Utils
open VahterBanBot.Bot
open VahterBanBot.Types
open VahterBanBot.StartupMessage
open VahterBanBot.UpdateChatAdmins
open VahterBanBot.FakeTgApi
open OpenTelemetry.Trace
open OpenTelemetry.Metrics
open OpenTelemetry.Resources
open Npgsql
open Azure.Monitor.OpenTelemetry.AspNetCore

type Root = class end

Dapper.FSharp.PostgreSQL.OptionTypes.register()
SqlMapper.AddTypeHandler(CallbackMessageTypeHandler());

let botConf =
    { BotToken = getEnv "BOT_TELEGRAM_TOKEN"
      Route = getEnvOr "BOT_HOOK_ROUTE" "/bot"
      SecretToken = getEnv "BOT_AUTH_TOKEN"
      LogsChannelId = getEnv "LOGS_CHANNEL_ID" |> int64
      ChatsToMonitor = getEnv "CHATS_TO_MONITOR" |> JsonConvert.DeserializeObject<_>
      AllowedUsers = getEnv "ALLOWED_USERS" |> JsonConvert.DeserializeObject<_>
      ShouldDeleteChannelMessages = getEnvOr "SHOULD_DELETE_CHANNEL_MESSAGES" "true" |> bool.Parse
      IgnoreSideEffects = getEnvOr "IGNORE_SIDE_EFFECTS" "false" |> bool.Parse
      UsePolling =  getEnvOr "USE_POLLING" "false" |> bool.Parse
      UseFakeTgApi = getEnvOr "USE_FAKE_TG_API" "false" |> bool.Parse
      CleanupOldMessages = getEnvOr "CLEANUP_OLD_MESSAGES" "true" |> bool.Parse
      CleanupInterval = getEnvOr "CLEANUP_INTERVAL_SEC" "86400" |> int |> TimeSpan.FromSeconds
      CleanupOldLimit = getEnvOr "CLEANUP_OLD_LIMIT_SEC" "259200" |> int |> TimeSpan.FromSeconds
      UpdateChatAdminsInterval = getEnvOrWith "UPDATE_CHAT_ADMINS_INTERVAL_SEC" None (int >> TimeSpan.FromSeconds >> Some)
      UpdateChatAdmins = getEnvOr "UPDATE_CHAT_ADMINS" "false" |> bool.Parse
      MlEnabled = getEnvOr "ML_ENABLED" "false" |> bool.Parse
      MlSeed = getEnvOrWith "ML_SEED" (Nullable<int>()) (int >> Nullable)
      MlSpamDeletionEnabled = getEnvOr "ML_SPAM_DELETION_ENABLED" "false" |> bool.Parse
      MlTrainBeforeDate = getEnvOrWith "ML_TRAIN_BEFORE_DATE" DateTime.UtcNow (DateTimeOffset.Parse >> _.UtcDateTime)
      MlTrainingSetFraction = getEnvOr "ML_TRAINING_SET_FRACTION" "0.2" |> float
      MlSpamThreshold = getEnvOr "ML_SPAM_THRESHOLD" "0.5" |> single
      MlWarningThreshold = getEnvOr "ML_WARNING_THRESHOLD" "0.0" |> single
      MlStopWordsInChats = getEnvOr "ML_STOP_WORDS_IN_CHATS" "{}" |> JsonConvert.DeserializeObject<_> }

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
    .AddHostedService<StartupMessage>()
    .AddHostedService<UpdateChatAdmins>()
    .AddSingleton<MachineLearning>()
    .AddHostedService<MachineLearning>(fun sp -> sp.GetRequiredService<MachineLearning>())
    .AddHttpClient("telegram_bot_client")
    .AddTypedClient(fun httpClient sp ->
        let options = TelegramBotClientOptions(botConf.BotToken)
        TelegramBotClient(options, httpClient) :> ITelegramBotClient
    )
    .ConfigureAdditionalHttpMessageHandlers(fun handlers sp ->
        if botConf.UseFakeTgApi then
            handlers.Add(fakeTgApi botConf)
    )

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
        use topActivity =
            botActivity
              .StartActivity("postUpdate")
              .SetTag("updateBodyObject", update)
              .SetTag("updateBodyJson", updateBodyJson)

        use scope = ctx.RequestServices.CreateScope()
        let telegramClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>()
        let ml = scope.ServiceProvider.GetRequiredService<MachineLearning>()
        let logger = ctx.GetLogger<Root>()
        try
            do! onUpdate telegramClient botConf (ctx.GetLogger "VahterBanBot.Bot") ml update
            %topActivity.SetTag("update-error", false)
        with e ->
            logger.LogError(e, $"Unexpected error while processing update: {updateBodyJson}")
            %topActivity.SetTag("update-error", true)

        return! Successful.OK() next ctx
    })
]

let app = builder.Build()

app.UseGiraffe(webApp)
let server = app.RunAsync()

// Dev mode only
if botConf.UsePolling then
    let telegramClient = app.Services.GetRequiredService<ITelegramBotClient>()
    let pollingHandler = {
        new IUpdateHandler with
          member x.HandleUpdateAsync (botClient: ITelegramBotClient, update: Update, cancellationToken: CancellationToken) =
            task {
                if update.Message <> null && update.Message.Type = MessageType.Text then
                    let ctx = app.Services.CreateScope()
                    let logger = ctx.ServiceProvider.GetRequiredService<ILogger<IUpdateHandler>>()
                    let client = ctx.ServiceProvider.GetRequiredService<ITelegramBotClient>()
                    let ml = ctx.ServiceProvider.GetRequiredService<MachineLearning>()
                    do! onUpdate client botConf logger ml update
            }
          member x.HandlePollingErrorAsync (botClient: ITelegramBotClient, ex: Exception, cancellationToken: CancellationToken) =
              Task.CompletedTask
    }
    telegramClient.StartReceiving(pollingHandler, null, CancellationToken.None)

server.Wait()
