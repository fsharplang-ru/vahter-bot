namespace BotInfra

open System
open System.Collections.Generic
open Microsoft.Extensions.DependencyInjection
open OpenTelemetry.Exporter
open OpenTelemetry.Metrics
open OpenTelemetry.Resources
open OpenTelemetry.Trace
open Npgsql
open Serilog
open Serilog.Enrichers.Span
open Serilog.Formatting.Compact

/// Shared observability setup for all bots.
module Observability =

    /// Configures Serilog with structured JSON logging and trace correlation.
    let configureSerilog (hostBuilder: Microsoft.Extensions.Hosting.IHostBuilder) =
        %hostBuilder.UseSerilog(fun context _services configuration ->
            %configuration
                .ReadFrom.Configuration(context.Configuration)
                .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
                .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithSpan()
                .WriteTo.Console(RenderedCompactJsonFormatter())
        )

    /// Configures OpenTelemetry tracing and metrics.
    let addBotOpenTelemetry
        (serviceName: string)
        (activitySourceName: string)
        (meterName: string)
        (services: IServiceCollection) =

        %services.AddOpenTelemetry()
            .WithTracing(fun builder ->
                %builder
                    .AddHttpClientInstrumentation(fun opts ->
                        opts.EnrichWithHttpRequestMessage <- fun activity req ->
                            let uri = req.RequestUri
                            if not (isNull uri) then
                                let host = uri.Host
                                let methodName = req.Method.Method
                                if host = "api.telegram.org" then
                                    // Telegram URLs embed the bot token in the path. Known shapes:
                                    //   /bot<TOKEN>/<methodName>           — API call
                                    //   /file/bot<TOKEN>/<filePath...>     — file download
                                    // Span *names* are very visible (and frequently shared in
                                    // screenshots), so be defensive against future URL shapes:
                                    // a Telegram bot token always contains ':' and no legitimate
                                    // API path segment does — treat any ':'-bearing segment as
                                    // a token and skip it. If the path also contains a "file"
                                    // segment, the call is a file download; otherwise pick the
                                    // first non-token segment as the method name.
                                    let segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                                    let nonTokenSegs = segs |> Array.filter (fun s -> not (s.Contains(':')))
                                    let displayName =
                                        if Array.contains "file" nonTokenSegs then
                                            "tg:fileDownload"
                                        else
                                            nonTokenSegs
                                            |> Array.tryHead
                                            |> Option.map (fun m -> $"tg:{m}")
                                            |> Option.defaultValue "tg:?"
                                    activity.DisplayName <- displayName
                                elif host.EndsWith("cognitiveservices.azure.com") then
                                    activity.DisplayName <- $"azure-ocr {methodName}"
                                elif host.EndsWith("openai.azure.com") then
                                    activity.DisplayName <- $"azure-openai {methodName}"
                                else
                                    activity.DisplayName <- $"{methodName} {host}"
                    )
                    .AddAspNetCoreInstrumentation()
                    .AddNpgsql()
                    .ConfigureResource(fun res ->
                        %res.AddAttributes [
                            KeyValuePair("service.name", getEnvOr "OTEL_SERVICE_NAME" serviceName)
                        ]
                    )
                    .AddSource(activitySourceName)
                getEnvWith "OTEL_EXPORTER_OTLP_ENDPOINT" (fun endpoint ->
                    %builder.AddOtlpExporter(fun options ->
                        options.Endpoint <- Uri(endpoint)
                        options.Protocol <- OtlpExportProtocol.Grpc
                    )
                )
                getEnvWith "OTEL_EXPORTER_CONSOLE" (fun v ->
                    if Boolean.Parse(v) then %builder.AddConsoleExporter()
                )
            )
            .WithMetrics(fun builder ->
                %builder
                    .AddHttpClientInstrumentation()
                    .AddAspNetCoreInstrumentation()
                    .AddMeter(meterName)
                getEnvWith "OTEL_EXPORTER_CONSOLE" (fun v ->
                    if Boolean.Parse(v) then %builder.AddConsoleExporter()
                )
                getEnvWith "OTEL_EXPORTER_OTLP_ENDPOINT" (fun endpoint ->
                    %builder.AddOtlpExporter(fun options ->
                        options.Endpoint <- Uri(endpoint)
                        options.Protocol <- OtlpExportProtocol.Grpc
                    )
                )
            )
        services
