module VahterBanBot.Utils

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging

let inline (~%) x = ignore x

let getEnv name =
    let value = Environment.GetEnvironmentVariable name
    if value = null then
        ArgumentException $"Required environment variable %s{name} not found"
        |> raise
    else
        value

let getEnvOr name defaultValue =
    let value = Environment.GetEnvironmentVariable name
    if value = null then defaultValue else value

let getEnvWith name action =
    let value = Environment.GetEnvironmentVariable name
    if value <> null then
        action value

let getEnvOrWith name defaultValue action =
    let value = Environment.GetEnvironmentVariable name
    if value <> null then
        action value
    else defaultValue

let prependUsername (s: string) =
    if isNull s then
        null
    elif s.StartsWith "@" then
        s
    else "@" + s

let pluralize n s =
    if n < 2.0 then
        s
    else
        $"%.0f{n} {s}s"

let timeSpanAsHumanReadable (ts: TimeSpan) =
    let totalSeconds = ts.TotalSeconds
    if totalSeconds < 60.0 then
        pluralize totalSeconds "second"
    elif totalSeconds < 3600.0 then
        pluralize ts.TotalMinutes "minute"
    elif totalSeconds < 86400.0 then
        pluralize ts.TotalHours "hour"
    else
        pluralize ts.TotalDays "day"

type Task<'x> with
    member this.Ignore() = task { let! _ = this in () }

let inline taskIgnore (t: Task<'x>) = t.Ignore()

type Telegram.Bot.Types.Update with
    member msg.EditedOrMessage =
        if isNull msg.EditedMessage then
            msg.Message
        else
            msg.EditedMessage

/// Runs an async action fire-and-forget style on the thread pool.
/// The action receives a CancellationToken — pass it to HttpClient and other I/O so
/// the work is cancelled when the timeout fires.
/// Any exception is logged; the caller is never affected.
let fireAndForget
    (logger: ILogger)
    (timeoutMs: int)
    (taskName: string)
    (action: CancellationToken -> Task) : unit =
    let cts = new CancellationTokenSource()
    cts.CancelAfter(timeoutMs)
    let _ =
        Task.Run(fun () ->
            let work = task {
                try
                    do! action cts.Token
                with
                | :? OperationCanceledException ->
                    logger.LogWarning("Fire-and-forget task '{TaskName}' timed out or was cancelled", taskName)
                | ex ->
                    logger.LogError(ex, "Fire-and-forget task '{TaskName}' failed", taskName)
                cts.Dispose()
            }
            work :> Task)
    ()

// needed for STJ
let jsonOptions =
    let baseOpts = Microsoft.AspNetCore.Http.Json.JsonOptions()
    Telegram.Bot.JsonBotAPI.Configure(baseOpts.SerializerOptions)
    
    // HACK TIME
    // there is a contradiction in Telegram.Bot library where User.IsBot is not nullable and required during deserialization,
    // but it is omitted when default on deserialization via settings setup in JsonBotAPI.Configure
    // so we'll override this setting explicitly
    baseOpts.SerializerOptions.DefaultIgnoreCondition <- System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    
    baseOpts.SerializerOptions
