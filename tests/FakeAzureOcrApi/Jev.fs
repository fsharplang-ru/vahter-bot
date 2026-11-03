module FakeAzureOcrApi.Jev

open System.Collections.Concurrent
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open FakeAzureOcrApi

let private script = ConcurrentQueue<ScriptedResponse>()

let setScript (ctx: HttpContext) = task {
    let! payload = JsonSerializer.DeserializeAsync<ResponseScriptDto>(ctx.Request.Body)
    match payload with
    | null -> ctx.Response.StatusCode <- 400
    | payload ->
        script.Clear()
        for response in payload.responses do script.Enqueue response
        do! ctx.Response.WriteAsync("{}")
}

let classify (ctx: HttpContext) = task {
    use reader = new StreamReader(ctx.Request.Body)
    let! body = reader.ReadToEndAsync()
    Store.logCall "POST" (string ctx.Request.Path) body
    if string ctx.Request.Headers.Authorization <> "Bearer synthetic-jev-key" then
        ctx.Response.StatusCode <- 401
    else
        let response =
            match script.TryDequeue() with
            | true, value -> value
            | _ -> { status = 500; body = "{}"; delayMs = 0; errorMode = "" }
        if response.delayMs > 0 then do! Task.Delay(response.delayMs, ctx.RequestAborted)
        if response.errorMode = "network" then ctx.Abort()
        else
            ctx.Response.StatusCode <- response.status
            ctx.Response.ContentType <- "application/json"
            do! ctx.Response.WriteAsync(response.body)
}
