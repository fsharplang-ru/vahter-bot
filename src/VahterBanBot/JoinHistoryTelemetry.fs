module VahterBanBot.JoinHistoryTelemetry

open System
open System.Collections.Generic
open System.Diagnostics
open System.Diagnostics.Metrics
open System.Net.Sockets
open System.Threading.Tasks
open BotInfra
open Microsoft.Extensions.Logging
open TL

let private histogram name description boundaries =
    Metrics.meter.CreateHistogram<float>(
        name, "s", description, null,
        InstrumentAdvice<float>(HistogramBucketBoundaries = boundaries))

let private operationDuration =
    histogram "vahter_join_history_operation_duration" "Elapsed time per history worker operation."
        [| 0.01; 0.025; 0.05; 0.1; 0.25; 0.5; 1.; 2.5; 5.; 10.; 30.; 60. |]

let private ageBuckets = [| 1.; 5.; 15.; 30.; 60.; 120.; 180.; 300.; 600.; 1800.; 3600.; 21600.; 86400. |]
let startDelay = histogram "vahter_join_history_start_delay" "Time past due at the first page of a bucket scan." ageBuckets
let completionAge = histogram "vahter_join_history_completion_age" "Time from the end of the Telegram minute to its final committed checkpoint." ageBuckets
let verifiedAge = histogram "vahter_join_history_verified_age" "Time from Telegram creation to verified survivor removal." ageBuckets
let floodWait = histogram "vahter_join_history_flood_wait" "MTProto requested cooldown per flood response." ageBuckets

let private ids = Metrics.meter.CreateCounter<int64>("vahter_join_history_ids_total", description = "Message IDs observed by scan stage, including repeat attempts.")
let private pages = Metrics.meter.CreateCounter<int64>("vahter_join_history_pages_total", description = "History page attempts by outcome.")
let private resets = Metrics.meter.CreateCounter<int64>("vahter_join_history_client_resets_total", description = "History clients discarded after connection failures.")
let private reactorErrors = Metrics.meter.CreateCounter<int64>("vahter_join_history_reactor_errors_total", description = "MTProto connections whose automatic recovery failed.")

let private tag name (value: string) = KeyValuePair<string,obj>(name, value)

let countIds stage count = ids.Add(int64 count, tag "stage" stage)
let countPage outcome = pages.Add(1L, tag "outcome" outcome)
let countReset reason = resets.Add(1L, tag "reason" reason)
let countReactorError () = reactorErrors.Add(1L)

let private exceptionTypes (ex: exn) =
    Seq.unfold (fun (e: exn) -> if isNull e then None else Some(e.GetType().FullName, e.InnerException)) ex
    |> Seq.truncate 8 |> String.concat " -> "

let logFailure (logger: ILogger) operation (ex: exn) =
    let rpc = match ex with :? RpcException as r -> Nullable r.Code | _ -> Nullable()
    let socket = match ex.GetBaseException() with :? SocketException as s -> string s.SocketErrorCode | _ -> ""
    logger.LogError("Join history {Operation} failed ({ErrorTypes}, RPC {RpcCode}, socket {SocketError}, HRESULT {HResult}); stack {ErrorStack}",
                    operation, exceptionTypes ex, rpc, socket, ex.HResult, ex.StackTrace)

let setTag (span: Activity) name (value: obj) =
    if not (isNull span) then %span.SetTag(name, value)

let markError (span: Activity) (ex: exn) =
    if not (isNull span) then
        %span.SetStatus(ActivityStatusCode.Error, ex.GetType().Name)
        %span.SetTag("error.type", exceptionTypes ex)
        %span.SetTag("exception.stacktrace", ex.StackTrace)
        match ex with
        | :? RpcException as rpc -> %span.SetTag("rpc.code", rpc.Code)
        | _ -> ()

let operation name (work: unit -> Task<'T>) = task {
    use span = Telemetry.botActivity.StartActivity("joinHistory." + name)
    let start = Stopwatch.GetTimestamp()
    let mutable outcome = "ok"
    try
        try
            return! work()
        with ex ->
            outcome <-
                match ex with
                | :? RpcException as rpc when rpc.Code = 420 -> "flood_wait"
                | :? OperationCanceledException -> "cancelled"
                | _ -> "error"
            markError span ex
            return! Task.FromException<'T>(ex)
    finally
        operationDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds, tag "operation" name, tag "outcome" outcome)
}
