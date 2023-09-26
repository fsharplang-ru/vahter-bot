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
