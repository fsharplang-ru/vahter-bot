open System
open System.Collections.Generic
open System.Diagnostics
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Dapper
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Microsoft.FSharp.Core
open Serilog.Enrichers.Span
open Telegram.Bot
open Telegram.Bot.Polling
open Telegram.Bot.Types
open Giraffe
open Microsoft.Extensions.DependencyInjection
open Telegram.Bot.Types.Enums
open VahterBanBot
open VahterBanBot.Cleanup
open VahterBanBot.ML
open VahterBanBot.ComputerVision
open VahterBanBot.Utils
open VahterBanBot.Bot
open VahterBanBot.Types
open VahterBanBot.StartupMessage
open VahterBanBot.UpdateChatAdmins
open VahterBanBot.FakeTgApi
open OpenTelemetry.Trace
open OpenTelemetry.Metrics
open OpenTelemetry.Resources
open OpenTelemetry.Exporter
open Npgsql
open Serilog
open Serilog.Formatting.Compact

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
      // Channels for vahter actions
      PotentialSpamChannelId = getEnv "POTENTIAL_SPAM_CHANNEL_ID" |> int64
      DetectedSpamChannelId = getEnv "DETECTED_SPAM_CHANNEL_ID" |> int64
      AllLogsChannelId = getEnv "ALL_LOGS_CHANNEL_ID" |> int64
      DetectedSpamCleanupAge = getEnvOr "DETECTED_SPAM_CLEANUP_AGE_HOURS" "24" |> int |> TimeSpan.FromHours
      ChatsToMonitor = getEnv "CHATS_TO_MONITOR" |> fromJson
      AllowedUsers = getEnv "ALLOWED_USERS" |> fromJson
      ShouldDeleteChannelMessages = getEnvOr "SHOULD_DELETE_CHANNEL_MESSAGES" "true" |> bool.Parse
      IgnoreSideEffects = getEnvOr "IGNORE_SIDE_EFFECTS" "false" |> bool.Parse
      UsePolling =  getEnvOr "USE_POLLING" "false" |> bool.Parse
      UseFakeApi =
          getEnvOr "USE_FAKE_TG_API" "false" // use old name for backward compatibility
          |> getEnvOr "USE_FAKE_API"
          |> bool.Parse
      CleanupOldMessages = getEnvOr "CLEANUP_OLD_MESSAGES" "true" |> bool.Parse
      CleanupInterval = getEnvOr "CLEANUP_INTERVAL_SEC" "86400" |> int64 |> TimeSpan.FromSeconds
      CleanupCheckInterval = getEnvOr "CLEANUP_CHECK_INTERVAL_SEC" "600" |> int64 |> TimeSpan.FromSeconds
      CleanupScheduledHour = getEnvOr "CLEANUP_SCHEDULED_HOUR_UTC" "22" |> int
      StatsScheduledHour = getEnvOr "STATS_SCHEDULED_HOUR_UTC" "8" |> int
      CleanupOldLimit = getEnvOr "CLEANUP_OLD_LIMIT_SEC" "259200" |> int64 |> TimeSpan.FromSeconds
      UpdateChatAdminsInterval = getEnvOrWith "UPDATE_CHAT_ADMINS_INTERVAL_SEC" None (int64 >> TimeSpan.FromSeconds >> Some)
      UpdateChatAdmins = getEnvOr "UPDATE_CHAT_ADMINS" "false" |> bool.Parse
      OcrEnabled = getEnvOr "OCR_ENABLED" "false" |> bool.Parse
      OcrMaxFileSizeBytes = getEnvOr "OCR_MAX_FILE_SIZE_BYTES" (string (20L * 1024L * 1024L)) |> int64
      AzureOcrEndpoint = getEnvOr "AZURE_OCR_ENDPOINT" ""
      AzureOcrKey = getEnvOr "AZURE_OCR_KEY" ""
      MlEnabled = getEnvOr "ML_ENABLED" "false" |> bool.Parse
      MlRetrainScheduledTime =
          let s = getEnvOr "ML_RETRAIN_SCHEDULED_TIME_UTC" "23:30"
          TimeOnly.Parse(s).ToTimeSpan()
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
      MlMaxNumberOfIterations = getEnvOr "ML_MAX_NUMBER_OF_ITERATIONS" "50" |> int
      MlCustomEmojiThreshold = getEnvOr "ML_CUSTOM_EMOJI_THRESHOLD" "20" |> int
      MlStopWordsInChats = getEnvOr "ML_STOP_WORDS_IN_CHATS" "{}" |> fromJson
      MlWeightDecayK = getEnvOr "ML_WEIGHT_DECAY_K" "0" |> float
      // Reaction spam detection
      ReactionSpamEnabled = getEnvOr "REACTION_SPAM_ENABLED" "false" |> bool.Parse
      ReactionSpamMinMessages = getEnvOr "REACTION_SPAM_MIN_MESSAGES" "10" |> int
      ReactionSpamMaxReactions = getEnvOr "REACTION_SPAM_MAX_REACTIONS" "5" |> int }

let validateApiKey (ctx : HttpContext) =
    match ctx.TryGetRequestHeader "X-Telegram-Bot-Api-Secret-Token" with
    | Some key when key = botConf.SecretToken -> true
    | _ -> false

let accessDenied = setStatusCode 401 >=> text "Access Denied"
let requiresApiKey = authorizeRequest validateApiKey accessDenied

let builder = WebApplication.CreateBuilder()

// Configure Serilog for structured logging with trace correlation
%builder.Host.UseSerilog(fun context services configuration ->
    %configuration
        .ReadFrom.Configuration(context.Configuration)
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.WithSpan()
        .WriteTo.Console(RenderedCompactJsonFormatter())
)

%builder.Services
    .AddSingleton(botConf)
    .AddGiraffe()
    // we need to customize Giraffe STJ settings to conform to the Telegram.Bot API
    .AddSingleton<Json.ISerializer>(Json.Serializer(jsonOptions))
    .ConfigureTelegramBot<Microsoft.AspNetCore.Http.Json.JsonOptions>(fun x -> x.SerializerOptions)
    // MachineLearning must start before CleanupService (loads model from DB on startup)
    .AddSingleton<MachineLearning>()
    .AddHostedService<MachineLearning>(fun sp -> sp.GetRequiredService<MachineLearning>())
    .AddHostedService<CleanupService>()
    .AddHostedService<StartupMessage>()
    .AddHostedService<UpdateChatAdmins>()

builder.Services
    .AddHttpClient<IComputerVision, AzureComputerVision>()
    .ConfigureAdditionalHttpMessageHandlers(fun handlers sp ->
        if botConf.UseFakeApi then
            handlers.Add(fakeApi botConf)
    )
    |> ignore

%builder.Services
    .AddHttpClient("telegram_bot_client")
    .AddTypedClient(fun httpClient sp ->
        let options = TelegramBotClientOptions(botConf.BotToken)
        TelegramBotClient(options, httpClient) :> ITelegramBotClient
    )
    .ConfigureAdditionalHttpMessageHandlers(fun handlers sp ->
        if botConf.UseFakeApi then
            handlers.Add(fakeApi botConf)
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
                        KeyValuePair("service.name", getEnvOr "OTEL_SERVICE_NAME" "vahter-bot")
                    ]
                )
                .AddSource(botActivity.Name)
            getEnvWith "OTEL_EXPORTER_OTLP_ENDPOINT" (fun endpoint ->
                %builder.AddOtlpExporter(fun options ->
                    options.Endpoint <- Uri(endpoint)
                    options.Protocol <- OtlpExportProtocol.Grpc
                )
            )
            getEnvWith "OTEL_EXPORTER_CONSOLE"  (bool.Parse >> fun otelConsole ->
                if otelConsole then %builder.AddConsoleExporter()
            )
        )
        .WithMetrics(fun builder ->
            %builder
                .AddHttpClientInstrumentation()
                .AddAspNetCoreInstrumentation()
                .AddMeter("VahterBanBot.Metrics")
            getEnvWith "OTEL_EXPORTER_CONSOLE"  (bool.Parse >> fun otelConsole ->
                if otelConsole then %builder.AddConsoleExporter()
            )
            getEnvWith "OTEL_EXPORTER_OTLP_ENDPOINT" (fun endpoint ->
                %builder.AddOtlpExporter(fun options ->
                    options.Endpoint <- Uri(endpoint)
                    options.Protocol <- OtlpExportProtocol.Grpc
                )
            )
        )

let botUser =
    DbUser.newUser(botConf.BotUserId, botConf.BotUserName)
    |> DB.upsertUser
    |> fun x -> x.Result

let webApp = choose [
    // Readiness check for ML model (used by startupProbe)
    GET >=> route "/ready" >=> fun next ctx -> task {
        let ml = ctx.GetService<MachineLearning>()
        if ml.IsReady then
            return! text "READY" next ctx
        else
            ctx.SetStatusCode 503
            return! text "ML model not ready yet" next ctx
    }
    
    // Health check (Azure compatibility - always returns OK if process is alive)
    GET >=> route "/health" >=> text "OK"
    
    // Fallback for any GET (Azure health checks on any route)
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
        let telegramClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>()
        let ml = scope.ServiceProvider.GetRequiredService<MachineLearning>()
        let computerVision = scope.ServiceProvider.GetRequiredService<IComputerVision>()
        let logger = ctx.GetLogger<Root>()
        try
            do! onUpdate botUser telegramClient botConf (ctx.GetLogger "VahterBanBot.Bot") ml computerVision update
            %topActivity.SetTag("update-error", false)
            %topActivity.SetStatus(ActivityStatusCode.Ok)
        with e ->
            logger.LogError(e, $"Unexpected error while processing update: {updateBodyJson}")
            %topActivity.SetStatus(ActivityStatusCode.Error)
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
                    let ocr = ctx.ServiceProvider.GetRequiredService<IComputerVision>()
                    do! onUpdate botUser client botConf logger ml ocr update
            }
          member this.HandleErrorAsync(botClient, ``exception``, source, cancellationToken) =
              Task.CompletedTask
    }
    telegramClient.StartReceiving(pollingHandler, null, CancellationToken.None)

server.Wait()
