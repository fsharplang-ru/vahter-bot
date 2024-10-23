namespace VahterBanBot.Tests

open System
open Microsoft.Extensions.Logging

type StringLogger() =
    let lockObj = obj()
    let messages = ResizeArray<string>() 
    interface ILogger with
        member this.BeginScope _ = null
        member this.IsEnabled _ = true
        member this.Log(logLevel, _eventId, state, ex, formatter) =
            lock lockObj (fun() ->
                messages.Add($"[{logLevel}] {formatter.Invoke(state, ex)}"))

    member _.ExtractMessages(): string = lock lockObj (fun() -> String.Join("\n", messages))