module VahterBanBot.Utils

open System

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

let prependUsername (s: string) =
    if isNull s then
        null
    elif s.StartsWith "@" then
        s
    else "@" + s

let timeSpanAsHumanReadable (ts: TimeSpan) =
    let totalSeconds = ts.TotalSeconds
    if totalSeconds < 60.0 then
        $"%.0f{totalSeconds} seconds"
    elif totalSeconds < 3600.0 then
        $"%.0f{ts.TotalMinutes} minutes"
    elif totalSeconds < 86400.0 then
        $"%.0f{ts.TotalHours} hours"
    else
        $"%.0f{ts.TotalDays} days"
