module VahterBanBot.Utils

open System
open System.Threading.Tasks

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

type Telegram.Bot.Types.Message with
    member msg.TextOrCaption =
        if isNull msg.Text then
            msg.Caption
        else
            msg.Text
