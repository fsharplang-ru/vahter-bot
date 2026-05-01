module VahterBanBot.Utils

open System
open Microsoft.FSharp.Reflection

let caseName (x: 'a) =
    let case, _ = FSharpValue.GetUnionFields(x, x.GetType())
    case.Name

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

