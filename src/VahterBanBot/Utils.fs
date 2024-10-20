module VahterBanBot.Utils

open System
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Funogram.Converters
open Funogram.Telegram.Types

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

let prependUsernameO (s: string option) =
    match s with
    | None -> null
    | Some s when s.StartsWith "@" -> s
    | Some s -> s

let prependUsername (s: string) = prependUsernameO(Some s)

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

type Funogram.Telegram.Types.Message with
    member msg.TextOrCaption =
        msg.Caption
        |> Option.orElse msg.Caption
        |> Option.defaultValue ""
        
    member msg.MessageId32 = // the documentation guarantees that this always fits into int32, Funogram disagrees
        Checked.int32 msg.MessageId
        
    member msg.FromId = // always should be set for chat messages, -1 just in case it is absent
        msg.From |> Option.map _.Id |> Option.defaultValue -1
        
    member msg.TextOrEmpty = msg.Text |> Option.defaultValue ""    
    member msg.IsFromBot = msg.From |> Option.map _.IsBot |> Option.defaultValue false
    member msg.FromFirstName = msg.From |> Option.map _.FirstName |> Option.defaultValue "" 
    member msg.FromUsername = msg.From |> Option.bind _.Username |> Option.defaultValue ""

type Funogram.Telegram.Types.Update with
    member msg.EditedOrMessage =
        msg.EditedMessage
        |> Option.orElse msg.EditedMessage

type Funogram.Telegram.Types.ChatMember with
    member this.User =
        match this with
        | Owner o -> o.User
        | Administrator a -> a.User
        | Member m -> m.User
        | Restricted r -> r.User
        | Left l -> l.User
        | Banned b -> b.User

// needed for STJ
let jsonOptions =
    // TODO[F]: Expose this from Funogram
    let o =
        JsonSerializerOptions(
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        )
    o.Converters.Add(DiscriminatedUnionConverterFactory())
    o.Converters.Add(UnixTimestampDateTimeConverter())
    o.Converters.Add(OptionConverterFactory())
    o