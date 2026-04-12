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
                    .AddHttpClientInstrumentation()
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
