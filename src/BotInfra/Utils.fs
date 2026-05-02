namespace BotInfra

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging

[<AutoOpen>]
module Utils =
    let inline (~%) x = ignore x

    let getEnv name =
        let value = Environment.GetEnvironmentVariable name
        if value = null then
            ArgumentException $"Required environment variable %s{name} not found"
            |> raise
        else
            value

    let getEnvOr (name: string) (defaultValue: string) =
        let value = Environment.GetEnvironmentVariable name
        if isNull value then defaultValue else value

    let getEnvWith (name: string) (f: string -> unit) =
        match Environment.GetEnvironmentVariable name with
        | null -> ()
        | v -> f v

    let getEnvOrWith name defaultValue action =
        let value = Environment.GetEnvironmentVariable name
        if value <> null then
            action value
        else defaultValue

    let getEnvOrBool (name: string) (defaultValue: bool) =
        match Environment.GetEnvironmentVariable name with
        | null -> defaultValue
        | v -> Boolean.Parse v

    let getEnvOrInt64 (name: string) (defaultValue: int64) =
        match Environment.GetEnvironmentVariable name with
        | null -> defaultValue
        | v -> Int64.Parse v

    type Task<'a> with
        member this.Ignore() = task { let! _ = this in () }

    let inline taskIgnore (t: Task<'a>) = t.Ignore()

    /// Spawns work on the thread pool without awaiting it. ExecutionContext flows
    /// through Task.Run so Activity.Current propagates: OTel spans created inside
    /// `work` still attach to the captured parent activity in the trace, even if
    /// that parent has already been disposed by the time the work runs.
    let inline fireAndForget (logger: ILogger) (name: string) (work: unit -> Task) : unit =
        %Task.Run(fun () ->
            task {
                try
                    do! work()
                with ex ->
                    logger.LogWarning(ex, "Fire-and-forget task '{Name}' failed", name)
            } :> Task
        )
