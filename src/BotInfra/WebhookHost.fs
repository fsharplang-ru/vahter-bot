namespace BotInfra

open System
open System.IO
open System.Net.Http
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection

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

    /// Registers Serilog, OTEL, the Funogram stack (via HttpClientFactory), and TimeProvider.
    let configureSharedServices (cfg: WebhookConfig) (builder: WebApplicationBuilder) =
        // Serilog
        Observability.configureSerilog builder.Host

        // Named HttpClient for Telegram API calls (consumed by BotConfig below).
        %builder.Services.AddHttpClient("telegram_bot_client")

        // Funogram stack
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

    /// Maps GET /health -> "OK" and POST {webhookRoute} -> validate + parseUpdate + onUpdate.
    /// Generic over the update type (bots pass FunogramJson.parseUpdate);
    /// parseUpdate returns None for malformed bodies (-> 400).
    /// onUpdate also receives the raw request body — the original Telegram JSON,
    /// which can carry fields the deserialized update type doesn't model yet.
    let mapWebhookEndpoints
        (cfg: WebhookConfig)
        (parseUpdate: string -> 'u option)
        (onUpdate: HttpContext -> string -> 'u -> Task<unit>)
        (app: WebApplication) =

        %app.MapGet("/health", Func<string>(fun () -> "OK"))

        %app.MapPost(cfg.WebhookRoute, Func<HttpContext, Task<IResult>>(fun ctx ->
            task {
                if not (validateApiKey cfg.SecretToken ctx) then
                    return Results.Text("Access Denied", statusCode = 401)
                else
                    use reader = new StreamReader(ctx.Request.Body)
                    let! rawBody = reader.ReadToEndAsync()
                    match parseUpdate rawBody with
                    | None -> return Results.BadRequest()
                    | Some update ->
                        do! onUpdate ctx rawBody update
                        return Results.Ok()
            }))
