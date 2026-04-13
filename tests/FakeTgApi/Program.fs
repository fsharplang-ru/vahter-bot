open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open FakeTgApi.Handlers

let builder = WebApplication.CreateBuilder()
let app = builder.Build()

// Telegram API emulation
app.MapPost("/bot{token}/{method}", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> handleTelegramMethod ctx)) |> ignore
app.MapPost("/bot{token}/{method}/{rest}", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> handleTelegramMethod ctx)) |> ignore
app.MapGet("/file/bot{token}/{*path}", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> handleFileDownload ctx)) |> ignore

// Test endpoints
app.MapGet("/test/calls", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> getCalls ctx)) |> ignore
app.MapDelete("/test/calls", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> clearCalls ctx)) |> ignore
app.MapPost("/test/mock/chatMember", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> setChatMember ctx)) |> ignore
app.MapPost("/test/mock/file", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> setFile ctx)) |> ignore
app.MapPost("/test/mock/methodError", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> setMethodError ctx)) |> ignore

app.MapGet("/health", Func<string>(fun () -> "OK")) |> ignore

app.Run()

