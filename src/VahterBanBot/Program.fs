open System
open System.Diagnostics
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Dapper
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
open VahterBanBot.Utils
open VahterBanBot.Bot
open VahterBanBot.Types
open VahterBanBot.StartupMessage
open VahterBanBot.UpdateChatAdmins
open BotInfra
open BotInfra.TelegramHelpers

type Root = class end

Dapper.FSharp.PostgreSQL.OptionTypes.register()

let botConfJsonOptions =
    let opts = JsonSerializerOptions(JsonSerializerDefaults.Web)
    opts.NumberHandling <- JsonNumberHandling.AllowReadingFromString
    opts
let fromJson<'a> (json: string) =
    JsonSerializer.Deserialize<'a>(json, botConfJsonOptions)

let loadDbSettings () =
    try
        DB.loadBotSettings().GetAwaiter().GetResult()
    with e ->
        eprintfn "[FATAL] Failed to load bot settings from database: %O" e
        reraise()

let mutable dbSettings = loadDbSettings()

let getSetting key =
    match dbSettings.TryGetValue key with
    | true, v -> v
    | _ -> null

let getSettingOr key def =
    match getSetting key with
    | null -> def
    | v -> v

let getRequiredSetting key =
    match getSetting key with
    | null -> failwithf "Required setting '%s' not found in bot_setting table" key
    | v -> v

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
    Time.provider <- BotInfra.Time.fromString (getSettingOr "BOT_FIXED_UTC_NOW" "")

let validateApiKey (ctx: HttpContext) =
    match ctx.Request.Headers.TryGetValue "X-Telegram-Bot-Api-Secret-Token" with
    | true, headerValues when headerValues.Count > 0 && headerValues[0] = botConf.SecretToken -> true
    | _ -> false

let builder = WebApplication.CreateBuilder()

// Configure Serilog for structured logging with trace correlation
Observability.configureSerilog builder.Host

%builder.Services
    .AddSingleton(botConf)
    .ConfigureTelegramBot<Microsoft.AspNetCore.Http.Json.JsonOptions>(fun x -> x.SerializerOptions)
    // MachineLearning must start before CleanupService (loads model from DB on startup)
    .AddSingleton<MachineLearning>()
    .AddHostedService<MachineLearning>(fun sp -> sp.GetRequiredService<MachineLearning>())
    .AddHostedService<CleanupService>()
    .AddHostedService<StartupMessage>()
    .AddHostedService<UpdateChatAdmins>()

%builder.Services.AddHttpClient<IComputerVision, AzureComputerVision>()
%builder.Services.AddHttpClient<ILlmTriage, AzureLlmTriage>()

%builder.Services
    .AddHttpClient("telegram_bot_client")
    .AddTypedClient(fun httpClient _sp ->
        let options =
            if isNull botConf.TelegramApiBaseUrl then
                TelegramBotClientOptions(botConf.BotToken)
            else
                TelegramBotClientOptions(botConf.BotToken, botConf.TelegramApiBaseUrl)
        TelegramBotClient(options, httpClient) :> ITelegramBotClient
    )

%Observability.addBotOpenTelemetry "vahter-bot" botActivity.Name "VahterBanBot.Metrics" builder.Services

// Ensure bot user record exists in DB (result not needed — identity comes from BotConfiguration.BotActor)
(DB.upsertUser botConf.BotUserId (Some botConf.BotUserName)).Result |> ignore

let app = builder.Build()

// Readiness check for ML model (used by startupProbe)
%app.MapGet("/ready", Func<HttpContext, IResult>(fun ctx ->
    let ml = ctx.RequestServices.GetRequiredService<MachineLearning>()
    if ml.IsReady then
        Results.Text "READY"
    else
        Results.Text("ML model not ready yet", statusCode = 503)
))

// Health check (Azure compatibility - always returns OK if process is alive)
%app.MapGet("/health", Func<string>(fun () -> "OK"))

// Fallback for any GET (Azure health checks on any route)
%app.MapFallback(Func<string>(fun () -> "OK"))

// Reload settings endpoint
%app.MapPost("/reload-settings", Func<HttpContext, IResult>(fun ctx ->
    if not (validateApiKey ctx) then
        Results.Text("Access Denied", statusCode = 401)
    else
        reloadSettings()
        ctx.RequestServices.GetRequiredService<ILogger<Root>>().LogInformation "Settings reloaded"
        Results.Ok "Settings reloaded"
))

// Main webhook endpoint
%app.MapPost(botConf.Route, Func<HttpContext, Task<IResult>>(fun ctx ->
    task {
        if not (validateApiKey ctx) then
            return Results.Text("Access Denied", statusCode = 401)
        else
            let logger = ctx.RequestServices.GetRequiredService<ILogger<Root>>()

            let! update = JsonSerializer.DeserializeAsync<Update>(ctx.Request.Body, telegramJsonOptions)

            if isNull update then
                return Results.BadRequest()
            else

            let updateBodyJson =
                try JsonSerializer.Serialize(update, options = telegramJsonOptions)
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
            let llmTriage = scope.ServiceProvider.GetRequiredService<ILlmTriage>()
            try
                do! onUpdate telegramClient botConf logger ml computerVision llmTriage update
                %topActivity.SetTag("update-error", false)
                %topActivity.SetStatus(ActivityStatusCode.Ok)
            with e ->
                logger.LogError(e, $"Unexpected error while processing update: {updateBodyJson}")
                %topActivity.SetStatus(ActivityStatusCode.Error)
                %topActivity.SetTag("update-error", true)

            return Results.Ok()
    }))

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
                    let llmTriage = ctx.ServiceProvider.GetRequiredService<ILlmTriage>()
                    do! onUpdate client botConf logger ml ocr llmTriage update
            }
          member this.HandleErrorAsync(botClient, ``exception``, source, cancellationToken) =
              Task.CompletedTask
    }
    telegramClient.StartReceiving(pollingHandler, null, CancellationToken.None)

server.Wait()
