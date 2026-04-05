module VahterBanBot.Utils

open System
open System.Globalization
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.FSharp.Reflection

let inline (~%) x = ignore x

let caseName (x: 'a) =
    let case, _ = FSharpValue.GetUnionFields(x, x.GetType())
    case.Name

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

/// Time helpers (testability via TimeProvider).
module Time =
    /// Environment variable that, when set, freezes `TimeProvider` to a constant UTC time.
    /// Format: any DateTimeOffset parseable string, recommended ISO-8601 like `2026-01-21T08:00:00Z`.
    [<Literal>]
    let FixedUtcNowEnvVar = "BOT_FIXED_UTC_NOW"

    type FixedTimeProvider(fixedUtcNow: DateTimeOffset) =
        inherit TimeProvider()
        override _.GetUtcNow() = fixedUtcNow

    let private parseFixedUtcNow (raw: string) =
        match DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal) with
        | true, dto -> dto
        | _ -> failwithf "Invalid %s value: '%s'. Expected ISO-8601 like 2026-01-21T08:00:00Z" FixedUtcNowEnvVar raw

    let fromString (raw: string) : TimeProvider =
        match raw with
        | null | "" -> TimeProvider.System
        | raw -> FixedTimeProvider(parseFixedUtcNow raw) :> TimeProvider

    // TODO: Replace mutable global with DI-injected TimeProvider when codebase moves to class-based services.
    let mutable provider = fromString (getEnvOr FixedUtcNowEnvVar "")
    let utcNow () = provider.GetUtcNow().UtcDateTime
