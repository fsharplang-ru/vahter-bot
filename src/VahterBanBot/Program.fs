open System
open System.Diagnostics
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Polling
open Telegram.Bot.Types
open Telegram.Bot.Types.Enums
open Microsoft.Extensions.DependencyInjection
open VahterBanBot
open VahterBanBot.Cleanup
open VahterBanBot.ML
open VahterBanBot.ComputerVision
open VahterBanBot.LlmTriage
open VahterBanBot.Telemetry
open VahterBanBot.Types
open VahterBanBot.StartupMessage
open VahterBanBot.UpdateChatAdmins
open BotInfra
open BotInfra.TelegramHelpers
open BotInfra.JsonSetup

type Root = class end

Dapper.FSharp.PostgreSQL.OptionTypes.register()

let connString = getEnv "DATABASE_URL"

let loadDbSettings () =
    try
        DbSettings.loadBotSettings(connString).GetAwaiter().GetResult()
    with e ->
        eprintfn "[FATAL] Failed to load bot settings from database: %O" e
        reraise()

let mutable dbSettings = loadDbSettings()

let getSetting key =
    let accessor = DbSettings.BotSettingsAccessor(dbSettings)
    accessor.GetSetting key

let getSettingOr key def =
    let accessor = DbSettings.BotSettingsAccessor(dbSettings)
    accessor.GetSettingOr(key, def)

let getRequiredSetting key =
    let accessor = DbSettings.BotSettingsAccessor(dbSettings)
    accessor.GetRequiredSetting key

let buildBotConf () =
    { BotToken = getEnv "BOT_TELEGRAM_TOKEN"
      Route = getSettingOr "BOT_HOOK_ROUTE" "/bot"
      SecretToken = getEnv "BOT_AUTH_TOKEN"
      BotUserId = getRequiredSetting "BOT_USER_ID" |> int64
      BotUserName = getRequiredSetting "BOT_USER_NAME"
      // Channels for vahter actions
      PotentialSpamChannelId = getRequiredSetting "POTENTIAL_SPAM_CHANNEL_ID" |> int64
      DetectedSpamChannelId = getRequiredSetting "DETECTED_SPAM_CHANNEL_ID" |> int64
      AllLogsChannelId = getRequiredSetting "ALL_LOGS_CHANNEL_ID" |> int64
      DetectedSpamCleanupAge = getSettingOr "DETECTED_SPAM_CLEANUP_AGE_HOURS" "24" |> int |> TimeSpan.FromHours
      ChatsToMonitor = getRequiredSetting "CHATS_TO_MONITOR" |> fromJson
      AllowedUsers = getRequiredSetting "ALLOWED_USERS" |> fromJson
      IgnoreSideEffects = getEnvOr "IGNORE_SIDE_EFFECTS" "false" |> bool.Parse
      UsePolling = getEnvOr "USE_POLLING" "false" |> bool.Parse
      TelegramApiBaseUrl =
          match getEnvOr "TELEGRAM_API_URL" "" with
          | "" -> null
          | v -> v
      CleanupInterval = getSettingOr "CLEANUP_INTERVAL_SEC" "86400" |> int64 |> TimeSpan.FromSeconds
      CleanupCheckInterval = getSettingOr "CLEANUP_CHECK_INTERVAL_SEC" "600" |> int64 |> TimeSpan.FromSeconds
      CleanupScheduledHour = getSettingOr "CLEANUP_SCHEDULED_HOUR_UTC" "22" |> int
      StatsScheduledHour = getSettingOr "STATS_SCHEDULED_HOUR_UTC" "8" |> int
      CleanupOldLimit = getSettingOr "CLEANUP_OLD_LIMIT_SEC" "259200" |> int64 |> TimeSpan.FromSeconds
      UpdateChatAdminsInterval =
          match getSetting "UPDATE_CHAT_ADMINS_INTERVAL_SEC" with
          | null -> None
          | v -> int64 v |> TimeSpan.FromSeconds |> Some
      UpdateChatAdmins = getSettingOr "UPDATE_CHAT_ADMINS" "false" |> bool.Parse
      OcrEnabled = getSettingOr "OCR_ENABLED" "false" |> bool.Parse
      OcrMaxFileSizeBytes = getSettingOr "OCR_MAX_FILE_SIZE_BYTES" (string (20L * 1024L * 1024L)) |> int64
      AzureOcrEndpoint = getSettingOr "AZURE_OCR_ENDPOINT" ""
      AzureOcrKey = getEnvOr "AZURE_OCR_KEY" ""
      MlEnabled = getSettingOr "ML_ENABLED" "false" |> bool.Parse
      MlRetrainScheduledTime =
          let s = getSettingOr "ML_RETRAIN_SCHEDULED_TIME_UTC" "23:30"
          TimeOnly.Parse(s).ToTimeSpan()
      MlSeed =
          match getSetting "ML_SEED" with
          | null -> Nullable<int>()
          | v -> int v |> Nullable
      MlSpamDeletionEnabled = getSettingOr "ML_SPAM_DELETION_ENABLED" "false" |> bool.Parse
      MlSpamAutobanEnabled = getSettingOr "ML_SPAM_AUTOBAN_ENABLED" "false" |> bool.Parse
      MlSpamAutobanCheckLastMsgCount = getSettingOr "ML_SPAM_AUTOBAN_CHECK_LAST_MSG_COUNT" "10" |> int
      MlSpamAutobanScoreThreshold = getSettingOr "ML_SPAM_AUTOBAN_SCORE_THRESHOLD" "-5.0" |> double
      MlTrainRandomSortData = getSettingOr "ML_TRAIN_RANDOM_SORT_DATA" "true" |> bool.Parse
      MlTrainInterval = getSettingOr "ML_TRAIN_INTERVAL_DAYS" "30" |> int |> TimeSpan.FromDays
      MlTrainCriticalMsgCount = getSettingOr "ML_TRAIN_CRITICAL_MSG_COUNT" "5" |> int
      MlTrainingSetFraction = getSettingOr "ML_TRAINING_SET_FRACTION" "0.2" |> float
      MlSpamThreshold = getSettingOr "ML_SPAM_THRESHOLD" "0.5" |> single
      MlWarningThreshold = getSettingOr "ML_WARNING_THRESHOLD" "0.0" |> single
      MlMaxNumberOfIterations = getSettingOr "ML_MAX_NUMBER_OF_ITERATIONS" "50" |> int
      MlCustomEmojiThreshold = getSettingOr "ML_CUSTOM_EMOJI_THRESHOLD" "20" |> int
      MlStopWordsInChats = getSettingOr "ML_STOP_WORDS_IN_CHATS" "{}" |> fromJson
      MlWeightDecayK = getSettingOr "ML_WEIGHT_DECAY_K" "0" |> float
      MlOldUserMsgCount = getSettingOr "ML_OLD_USER_MSG_COUNT" "50" |> int
      // Reaction spam detection
      ReactionSpamEnabled = getSettingOr "REACTION_SPAM_ENABLED" "false" |> bool.Parse
      ReactionSpamMinMessages = getSettingOr "REACTION_SPAM_MIN_MESSAGES" "10" |> int
      ReactionSpamMaxReactions = getSettingOr "REACTION_SPAM_MAX_REACTIONS" "5" |> int
      // Forward spam detection
      ForwardSpamDetectionEnabled = getSettingOr "FORWARD_SPAM_DETECTION_ENABLED" "true" |> bool.Parse
      // Inline keyboard spam detection
      InlineKeyboardSpamDetectionEnabled = getSettingOr "INLINE_KEYBOARD_SPAM_DETECTION_ENABLED" "true" |> bool.Parse
      // LLM shadow triage
      LlmTriageEnabled      = getSettingOr "LLM_TRIAGE_ENABLED" "false" |> bool.Parse
      AzureOpenAiEndpoint   = getSettingOr "AZURE_OPENAI_ENDPOINT" ""
      AzureOpenAiKey        = getEnvOr "AZURE_OPENAI_KEY" ""
      AzureOpenAiDeployment = getSettingOr "AZURE_OPENAI_DEPLOYMENT" "gpt-4o-mini"
      LlmChatDescriptions   = getSettingOr "CHAT_DESCRIPTIONS_JSON" "{}" |> fromJson
      BanExpiryDays         = getSettingOr "BAN_EXPIRY_DAYS" "7" |> int }

// TODO: Replace mutable global with DI-registered BotConfiguration when codebase moves to class-based services.
let mutable botConf = buildBotConf()

let reloadSettings () =
    dbSettings <- loadDbSettings()
    botConf <- buildBotConf()

let webhookCfg: WebhookConfig =
    { BotToken = botConf.BotToken
      SecretToken = botConf.SecretToken
      TelegramApiBaseUrl = botConf.TelegramApiBaseUrl
      OtelServiceName = "vahter-bot"
      ActivitySourceName = botActivity.Name
      MeterName = "VahterBanBot.Metrics"
      WebhookRoute = botConf.Route }

let builder = WebApplication.CreateBuilder()

WebhookHost.configureSharedServices webhookCfg builder

%builder.Services
    .AddSingleton(botConf)
    .AddSingleton<DbService>(fun sp ->
        DbService(connString, sp.GetRequiredService<TimeProvider>()))
    .AddSingleton<BotService>()
    // MachineLearning must start before CleanupService (loads model from DB on startup)
    .AddSingleton<MachineLearning>()
    .AddHostedService<MachineLearning>(fun sp -> sp.GetRequiredService<MachineLearning>())
    .AddHostedService<CleanupService>()
    .AddHostedService<StartupMessage>()
    .AddHostedService<UpdateChatAdmins>()

// OCR: register shared IBotOcr, then the VahterBanBot adapter that keeps the IComputerVision interface
%builder.Services.AddSingleton<BotOcrConfig>(
    { OcrEnabled = botConf.OcrEnabled
      AzureOcrEndpoint = botConf.AzureOcrEndpoint
      AzureOcrKey = botConf.AzureOcrKey })
%builder.Services.AddHttpClient<IBotOcr, AzureBotOcr>()
%builder.Services.AddSingleton<IComputerVision, BotOcrComputerVision>()
%builder.Services.AddHttpClient<ILlmTriage, AzureLlmTriage>()

let app = builder.Build()

// Ensure bot user record exists in DB (result not needed -- identity comes from BotConfiguration.BotActor)
(app.Services.GetRequiredService<DbService>().UpsertUser(botConf.BotUserId, Some botConf.BotUserName)).Result |> ignore

// Readiness check for ML model (used by startupProbe)
%app.MapGet("/ready", Func<HttpContext, IResult>(fun ctx ->
    let ml = ctx.RequestServices.GetRequiredService<MachineLearning>()
    if ml.IsReady then
        Results.Text "READY"
    else
        Results.Text("ML model not ready yet", statusCode = 503)
))

// Fallback for any GET (Azure health checks on any route)
%app.MapFallback(Func<string>(fun () -> "OK"))

// Reload settings endpoint
%app.MapPost("/reload-settings", Func<HttpContext, IResult>(fun ctx ->
    if not (WebhookHost.validateApiKey webhookCfg.SecretToken ctx) then
        Results.Text("Access Denied", statusCode = 401)
    else
        reloadSettings()
        // Update the runtime TimeProvider so BOT_FIXED_UTC_NOW changes take effect immediately.
        // This is a no-op in production (setting is empty → System clock), but lets integration
        // tests advance time without restarting the container.
        let mtp = ctx.RequestServices.GetRequiredService<Time.MutableTimeProvider>()
        mtp.SetInner(Time.fromString (getSettingOr "BOT_FIXED_UTC_NOW" ""))
        ctx.RequestServices.GetRequiredService<ILogger<Root>>().LogInformation "Settings reloaded"
        Results.Ok "Settings reloaded"
))

// Main webhook endpoint with bot-specific update handling
WebhookHost.mapWebhookEndpoints webhookCfg (fun ctx update ->
    task {
        let logger = ctx.RequestServices.GetRequiredService<ILogger<Root>>()

        let updateBodyJson =
            try JsonSerializer.Serialize(update, options = telegramJsonOptions)
            with e -> e.Message
        use topActivity =
            botActivity
              .StartActivity("postUpdate")
              .SetTag("updateBodyObject", update)
              .SetTag("updateBodyJson", updateBodyJson)

        let bot = ctx.RequestServices.GetRequiredService<BotService>()
        try
            do! bot.OnUpdate(update)
            %topActivity.SetTag("update-error", false)
            %topActivity.SetStatus(ActivityStatusCode.Ok)
        with e ->
            logger.LogError(e, $"Unexpected error while processing update: {updateBodyJson}")
            %topActivity.SetStatus(ActivityStatusCode.Error)
            %topActivity.SetTag("update-error", true)
    }) app

let server = app.RunAsync()

// Dev mode only
if botConf.UsePolling then
    let telegramClient = app.Services.GetRequiredService<ITelegramBotClient>()
    let pollingHandler = {
        new IUpdateHandler with
          member x.HandleUpdateAsync (botClient: ITelegramBotClient, update: Update, cancellationToken: CancellationToken) =
            task {
                if update.Message <> null && update.Message.Type = MessageType.Text then
                    let bot = app.Services.GetRequiredService<BotService>()
                    do! bot.OnUpdate(update)
            }
          member this.HandleErrorAsync(botClient, ``exception``, source, cancellationToken) =
              Task.CompletedTask
    }
    telegramClient.StartReceiving(pollingHandler, null, CancellationToken.None)

server.Wait()
