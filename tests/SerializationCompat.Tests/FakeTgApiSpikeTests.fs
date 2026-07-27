module SerializationCompat.Tests.FakeTgApiSpike

open System
open System.Net.Http
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Xunit
open BotInfra
open Funogram.Types
open Funogram.Telegram.Types

module Req = Funogram.Telegram.Req

/// Spike gate for the migration: proves Funogram's HTTP layer speaks to the existing
/// FakeTgApi implementation unchanged — URL shape ({base}/bot{token}/{method}), JSON
/// request bodies, and response parsing — by hosting FakeTgApi's real handlers in-proc.
type FakeTgApiSpikeTests() =

    let mutable app: WebApplication | null = null
    let mutable config = Unchecked.defaultof<BotConfig>

    interface IAsyncLifetime with
        member _.InitializeAsync() : ValueTask =
            ValueTask(task {
                let builder = WebApplication.CreateBuilder()
                %builder.Logging.ClearProviders()
                let a = builder.Build()
                %a.MapPost("/bot{token}/{method}", Func<HttpContext, Task>(fun ctx -> FakeTgApi.Handlers.handleTelegramMethod ctx))
                %a.MapPost("/bot{token}/{method}/{rest}", Func<HttpContext, Task>(fun ctx -> FakeTgApi.Handlers.handleTelegramMethod ctx))
                %a.MapGet("/file/bot{token}/{*path}", Func<HttpContext, Task>(fun ctx -> FakeTgApi.Handlers.handleFileDownload ctx))
                a.Urls.Add "http://127.0.0.1:0"
                do! a.StartAsync()
                let addr =
                    a.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses
                    |> Seq.head
                app <- a
                config <-
                    { IsTest = false
                      Token = "test-token"
                      Offset = None
                      Limit = None
                      Timeout = Some 10000L
                      AllowedUpdates = None
                      OnError = ignore
                      ApiEndpointUrl = Uri($"{addr}/bot")
                      Client = new HttpClient()
                      WebHook = None
                      RequestLogger = None }
            } :> Task)
        member _.DisposeAsync() : ValueTask =
            ValueTask(task {
                match app with
                | null -> ()
                | a -> do! a.StopAsync()
            } :> Task)

    [<Fact>]
    member _.``sendMessage: Funogram request hits FakeTgApi route and response parses`` () =
        task {
            let! result =
                Req.SendMessage.Make(123L, "hello from funogram")
                |> Funogram.Api.api config
                |> Async.StartAsTask
            match result with
            | Ok msg ->
                Assert.Equal(123L, msg.Chat.Id)
                Assert.True(msg.MessageId > 0L)
            | Error e -> Assert.Fail $"FakeTgApi rejected Funogram sendMessage: {e.ErrorCode} {e.Description}"
        }

    [<Fact>]
    member _.``deleteMessage: simple-ok methods work`` () =
        task {
            let! result =
                Req.DeleteMessage.Make(123L, 42L)
                |> Funogram.Api.api config
                |> Async.StartAsTask
            match result with
            | Ok ok -> Assert.True ok
            | Error e -> Assert.Fail $"FakeTgApi rejected Funogram deleteMessage: {e.ErrorCode} {e.Description}"
        }

    [<Fact>]
    member _.``ITelegramApi wrapper: CallExn returns the payload and DownloadFile fetches bytes`` () =
        task {
            let tg =
                BotInfra.TelegramApi(
                    config,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<BotInfra.TelegramApi>.Instance)
                :> ITelegramApi

            let! sent = tg.CallExn(Req.SendMessage.Make(321L, "via wrapper"))
            Assert.Equal(321L, sent.Chat.Id)

            // FakeTgApi echoes the request path for unknown file ids — enough to prove
            // the {origin}/file/bot{token}/{path} URL derivation and byte download work.
            let! bytes = tg.DownloadFile "photos/some-file.jpg"
            Assert.NotEmpty bytes
        }

    [<Fact>]
    member _.``getChatMember: status discriminates the ChatMember DU case`` () =
        task {
            let! result =
                Req.GetChatMember.Make(123L, 456L)
                |> Funogram.Api.api config
                |> Async.StartAsTask
            match result with
            | Ok member' ->
                // Funogram 3.0.5+ picks the DU case from the "status" discriminator
                // (the 3.0.4 converter misdiscriminated by field-shape overlap).
                // FakeTgApi defaults unknown users to "member".
                match member' with
                | ChatMember.Member m ->
                    Assert.Equal("member", m.Status)
                    Assert.Equal(456L, m.User.Id)
                | other -> Assert.Fail $"status \"member\" deserialized as %A{other} instead of ChatMember.Member"
            | Error e -> Assert.Fail $"FakeTgApi rejected Funogram getChatMember: {e.ErrorCode} {e.Description}"
        }
