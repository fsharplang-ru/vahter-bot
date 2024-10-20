#nowarn "44" // open telemetry is going crazy with warnings

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Serialization
open Dapper
open Funogram.Telegram.Bot
open Funogram.Telegram.Types
open Funogram.Types
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Microsoft.FSharp.Core
open Giraffe
open Microsoft.Extensions.DependencyInjection
open VahterBanBot
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

let botConfJsonOptions =
    let opts = JsonSerializerOptions(JsonSerializerDefaults.Web)
    opts.NumberHandling <- JsonNumberHandling.AllowReadingFromString
    opts
let fromJson<'a> (json: string) =
    JsonSerializer.Deserialize<'a>(json, botConfJsonOptions)

let botConf =
    { BotToken = getEnv "BOT_TELEGRAM_TOKEN"
      Route = getEnvOr "BOT_HOOK_ROUTE" "/bot"
      SecretToken = getEnv "BOT_AUTH_TOKEN"
      BotUserId = getEnv "BOT_USER_ID" |> int64
      BotUserName = getEnv "BOT_USER_NAME"
      LogsChannelId = getEnv "LOGS_CHANNEL_ID" |> int64
      ChatsToMonitor = getEnv "CHATS_TO_MONITOR" |> fromJson
      AllowedUsers = getEnv "ALLOWED_USERS" |> fromJson
      ShouldDeleteChannelMessages = getEnvOr "SHOULD_DELETE_CHANNEL_MESSAGES" "true" |> bool.Parse
      IgnoreSideEffects = getEnvOr "IGNORE_SIDE_EFFECTS" "false" |> bool.Parse
      UseFakeTgApi = getEnvOr "USE_FAKE_TG_API" "false" |> bool.Parse
      CleanupOldMessages = getEnvOr "CLEANUP_OLD_MESSAGES" "true" |> bool.Parse
      CleanupInterval = getEnvOr "CLEANUP_INTERVAL_SEC" "86400" |> int |> TimeSpan.FromSeconds
      CleanupOldLimit = getEnvOr "CLEANUP_OLD_LIMIT_SEC" "259200" |> int |> TimeSpan.FromSeconds
      UpdateChatAdminsInterval = getEnvOrWith "UPDATE_CHAT_ADMINS_INTERVAL_SEC" None (int >> TimeSpan.FromSeconds >> Some)
      UpdateChatAdmins = getEnvOr "UPDATE_CHAT_ADMINS" "false" |> bool.Parse
      MlEnabled = getEnvOr "ML_ENABLED" "false" |> bool.Parse
      MlRetrainInterval = getEnvOrWith "ML_RETRAIN_INTERVAL_SEC" None (int >> TimeSpan.FromSeconds >> Some)
      MlSeed = getEnvOrWith "ML_SEED" (Nullable<int>()) (int >> Nullable)
      MlSpamDeletionEnabled = getEnvOr "ML_SPAM_DELETION_ENABLED" "false" |> bool.Parse
      MlSpamAutobanEnabled = getEnvOr "ML_SPAM_AUTOBAN_ENABLED" "false" |> bool.Parse
      MlSpamAutobanCheckLastMsgCount = getEnvOr "ML_SPAM_AUTOBAN_CHECK_LAST_MSG_COUNT" "10" |> int
      MlSpamAutobanScoreThreshold = getEnvOr "ML_SPAM_AUTOBAN_SCORE_THRESHOLD" "-5.0" |> double
      MlTrainRandomSortData = getEnvOr "ML_TRAIN_RANDOM_SORT_DATA" "true" |> bool.Parse
      MlTrainInterval = getEnvOr "ML_TRAIN_INTERVAL_DAYS" "30" |> int |> TimeSpan.FromDays
      MlTrainCriticalMsgCount = getEnvOr "ML_TRAIN_CRITICAL_MSG_COUNT" "5" |> int
      MlTrainingSetFraction = getEnvOr "ML_TRAINING_SET_FRACTION" "0.2" |> float
      MlSpamThreshold = getEnvOr "ML_SPAM_THRESHOLD" "0.5" |> single
      MlWarningThreshold = getEnvOr "ML_WARNING_THRESHOLD" "0.0" |> single
      MlStopWordsInChats = getEnvOr "ML_STOP_WORDS_IN_CHATS" "{}" |> fromJson }

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
    // we need to customize Giraffe STJ settings to conform to the Telegram.Bot API
    .AddSingleton<Json.ISerializer>(Json.Serializer(jsonOptions))
    .AddSingleton<_>(
        let config = { Config.defaultConfig with Token = botConf.BotToken }
        TelegramBotClient(config)
    )
    .AddHostedService<CleanupService>()
    .AddHostedService<StartupMessage>()
    .AddHostedService<UpdateChatAdmins>()
    .AddSingleton<MachineLearning>()
    .AddHostedService<MachineLearning>(fun sp -> sp.GetRequiredService<MachineLearning>())
    // TODO[F]: Figure out these
    .AddHttpClient("telegram_bot_client")
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

let botUser =
    DbUser.newUser(botConf.BotUserId, botConf.BotUserName)
    |> DB.upsertUser
    |> fun x -> x.Result

let webApp = choose [
    // need for Azure health checks on any route
    GET >=> text "OK"

    POST >=> route botConf.Route >=> requiresApiKey >=> bindJson<Update> (fun update next ctx -> task {
        let updateBodyJson =
            try JsonSerializer.Serialize(update, options = jsonOptions)
            with e -> e.Message
        use topActivity =
            botActivity
              .StartActivity("postUpdate")
              .SetTag("updateBodyObject", update)
              .SetTag("updateBodyJson", updateBodyJson)

        use scope = ctx.RequestServices.CreateScope()
        let telegramClient = scope.ServiceProvider.GetRequiredService<TelegramBotClient>()
        let ml = scope.ServiceProvider.GetRequiredService<MachineLearning>()
        let logger = ctx.GetLogger<Root>()
        try
            do! onUpdate botUser telegramClient botConf (ctx.GetLogger "VahterBanBot.Bot") ml update
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

server.Wait()
