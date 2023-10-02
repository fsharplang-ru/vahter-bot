open System.Collections.Generic
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Microsoft.FSharp.Core
open Newtonsoft.Json
open Telegram.Bot
open Telegram.Bot.Types
open Giraffe
open Microsoft.Extensions.DependencyInjection
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
    // need for Azure health checks on / route
    GET >=> route "/" >=> text "OK"
    
    requiresApiKey >=> choose [
        POST >=> route botConf.Route >=> bindJson<Update> (fun update next ctx -> task {
            use scope = ctx.RequestServices.CreateScope()
            let telegramClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>()
            let logger = ctx.GetLogger<Root>()
            try
                do! onUpdate telegramClient botConf (ctx.GetLogger "VahterBanBot.Bot") update.Message
            with e ->
                logger.LogError(e, "Unexpected error while processing update")
            return! Successful.OK() next ctx
        })
    ]
]

let app = builder.Build()

app.UseGiraffe(webApp)
let server = app.RunAsync()

let telegramClient = app.Services.GetRequiredService<ITelegramBotClient>()

let startLogMsg =
    let sb = System.Text.StringBuilder()
    %sb.AppendLine("Bot started with following configuration")
    %sb.AppendLine("AllowedUsers:")
    for KeyValue(username, userId) in botConf.AllowedUsers do
        %sb.AppendLine($"  {prependUsername username} ({userId})")
    %sb.AppendLine("ChatsToMonitor:")
    for KeyValue(username, chatId) in botConf.ChatsToMonitor do
        %sb.AppendLine($"  {prependUsername username} ({chatId})")
    sb.ToString()

app.Logger.LogInformation startLogMsg
telegramClient.SendTextMessageAsync(ChatId(botConf.LogsChannelId), startLogMsg).Wait()

server.Wait()
