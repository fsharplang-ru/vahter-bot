namespace BotInfra

open System
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

        // TimeProvider (frozen in tests via BOT_FIXED_UTC_NOW)
        %builder.Services.AddSingleton<TimeProvider>(fun _sp -> Time.fromEnvironment())

        // OpenTelemetry
        %Observability.addBotOpenTelemetry cfg.OtelServiceName cfg.ActivitySourceName cfg.MeterName builder.Services

    /// Validates the X-Telegram-Bot-Api-Secret-Token header.
    let validateApiKey (secretToken: string) (ctx: HttpContext) =
        match ctx.Request.Headers.TryGetValue "X-Telegram-Bot-Api-Secret-Token" with
        | true, headerValues when headerValues.Count > 0 && headerValues[0] = secretToken -> true
        | _ -> false

    /// Maps GET /health -> "OK" and POST {webhookRoute} -> validate + deserialize + onUpdate.
    let mapWebhookEndpoints
        (cfg: WebhookConfig)
        (onUpdate: HttpContext -> Update -> Task<unit>)
        (app: WebApplication) =

        %app.MapGet("/health", Func<string>(fun () -> "OK"))

        %app.MapPost(cfg.WebhookRoute, Func<HttpContext, Task<IResult>>(fun ctx ->
            task {
                if not (validateApiKey cfg.SecretToken ctx) then
                    return Results.Text("Access Denied", statusCode = 401)
                else
                    let! update = JsonSerializer.DeserializeAsync<Update>(ctx.Request.Body, telegramJsonOptions)

                    if isNull update then
                        return Results.BadRequest()
                    else
                        do! onUpdate ctx update
                        return Results.Ok()
            }))
