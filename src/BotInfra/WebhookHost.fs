namespace BotInfra

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Telegram.Bot
open Telegram.Bot.Types
open BotInfra.TelegramHelpers

/// Configuration for the shared webhook pipeline.
type WebhookConfig =
    { BotToken: string
      SecretToken: string
      /// Null means production Telegram API.
      TelegramApiBaseUrl: string | null
      OtelServiceName: string
      ActivitySourceName: string
      MeterName: string
      /// The POST route that Telegram calls, e.g. "/bot".
      WebhookRoute: string }

/// Shared ASP.NET Core webhook pipeline used by all bots.
module WebhookHost =

    /// Registers Serilog, OTEL, TelegramBotClient (via HttpClientFactory), and TimeProvider.
    let configureSharedServices (cfg: WebhookConfig) (builder: WebApplicationBuilder) =
        // Serilog
        Observability.configureSerilog builder.Host

        // Telegram JSON via the standard extension
        %builder.Services.ConfigureTelegramBot<Microsoft.AspNetCore.Http.Json.JsonOptions>(fun x -> x.SerializerOptions)

        // TelegramBotClient via HttpClientFactory
        %builder.Services
            .AddHttpClient("telegram_bot_client")
            .AddTypedClient(fun httpClient (_sp: IServiceProvider) ->
                let options =
                    if isNull cfg.TelegramApiBaseUrl then
                        TelegramBotClientOptions(cfg.BotToken)
                    else
                        TelegramBotClientOptions(cfg.BotToken, cfg.TelegramApiBaseUrl)
                TelegramBotClient(options, httpClient) :> ITelegramBotClient
            )

        // Funogram stack (dual-registered during the Telegram.Bot -> Funogram migration;
        // the Telegram.Bot block above is deleted once both bots are migrated).
        %builder.Services.AddSingleton<Funogram.Types.BotConfig>(Func<IServiceProvider, Funogram.Types.BotConfig>(fun sp ->
            let httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("telegram_bot_client")
            { IsTest = false
              Token = cfg.BotToken
              Offset = None
              Limit = None
              Timeout = Some 60000L
              AllowedUpdates = None
              OnError = ignore
              ApiEndpointUrl =
                  match cfg.TelegramApiBaseUrl with
                  | null -> Uri "https://api.telegram.org/bot"
                  // FakeTgApi override: its routes are /bot{token}/{method} and /file/bot{token}/{*path}
                  | baseUrl -> Uri(baseUrl.TrimEnd('/') + "/bot")
              Client = httpClient
              WebHook = None
              RequestLogger = None }))
        %builder.Services.AddSingleton<ITelegramApi, TelegramApi>()

        // TimeProvider (frozen in tests via BOT_FIXED_UTC_NOW).
        // Registered as MutableTimeProvider so the reload endpoint can advance time at runtime.
        let mtp = Time.MutableTimeProvider(Time.fromEnvironment())
        %builder.Services.AddSingleton<Time.MutableTimeProvider>(mtp)
        %builder.Services.AddSingleton<TimeProvider>(mtp :> TimeProvider)

        // OpenTelemetry
        %Observability.addBotOpenTelemetry cfg.OtelServiceName cfg.ActivitySourceName cfg.MeterName builder.Services

    /// Validates the X-Telegram-Bot-Api-Secret-Token header.
    let validateApiKey (secretToken: string) (ctx: HttpContext) =
        match ctx.Request.Headers.TryGetValue "X-Telegram-Bot-Api-Secret-Token" with
        | true, headerValues when headerValues.Count > 0 && headerValues[0] = secretToken -> true
        | _ -> false

    /// Telegram.Bot-flavored webhook body parser (pre-migration bots); Funogram bots
    /// use FunogramJson.parseUpdate instead.
    let parseTelegramBotUpdate (body: Stream) : Task<Update option> =
        task {
            try
                let! update = JsonSerializer.DeserializeAsync<Update>(body, telegramJsonOptions)
                return Option.ofObj update
            with :? JsonException ->
                return None
        }

    /// Maps GET /health -> "OK" and POST {webhookRoute} -> validate + parseUpdate + onUpdate.
    /// Generic over the update type so Telegram.Bot and Funogram bots share the pipeline
    /// during the migration; parseUpdate returns None for malformed bodies (-> 400).
    let mapWebhookEndpoints
        (cfg: WebhookConfig)
        (parseUpdate: Stream -> Task<'u option>)
        (onUpdate: HttpContext -> 'u -> Task<unit>)
        (app: WebApplication) =

        %app.MapGet("/health", Func<string>(fun () -> "OK"))

        %app.MapPost(cfg.WebhookRoute, Func<HttpContext, Task<IResult>>(fun ctx ->
            task {
                if not (validateApiKey cfg.SecretToken ctx) then
                    return Results.Text("Access Denied", statusCode = 401)
                else
                    match! parseUpdate ctx.Request.Body with
                    | None -> return Results.BadRequest()
                    | Some update ->
                        do! onUpdate ctx update
                        return Results.Ok()
            }))
