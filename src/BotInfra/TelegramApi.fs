namespace BotInfra

open System
open System.IO
open System.Net.Http
open System.Reflection
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Funogram.Types

/// Funogram's composed JsonSerializerOptions (snake_case wire format + DU/unix-date/
/// option converters). Internal in Funogram 3.0.4, so grabbed once via reflection —
/// using the exact instance the library serializes with means our reads can never
/// drift from its writes. Swap for the public accessor once Funogram exposes one.
module FunogramJson =
    let options =
        let prop =
            Assembly.Load("Funogram")
                .GetType("Funogram.Tools")
                .GetProperty("options", BindingFlags.Static ||| BindingFlags.NonPublic ||| BindingFlags.Public)
        match prop with
        | null -> failwith "Funogram.Tools.options not found — Funogram internals changed, update FunogramJson"
        | p -> p.GetValue(null) :?> JsonSerializerOptions

    let serialize<'a> (value: 'a) : string =
        JsonSerializer.Serialize(value, options)

    let deserialize<'a> (json: string) : 'a =
        JsonSerializer.Deserialize<'a>(json, options)

    /// Webhook-body deserializer for Funogram-based bots. Returns None on malformed input.
    let parseUpdate (body: Stream) : Task<Funogram.Telegram.Types.Update option> =
        task {
            try
                let! update = JsonSerializer.DeserializeAsync<Funogram.Telegram.Types.Update>(body, options)
                return Some update
            with :? JsonException ->
                return None
        }

/// Raised by ITelegramApi.CallExn on a Telegram API error — preserves Telegram.Bot's
/// throwing semantics so existing try/with guards and per-update catch-alls keep
/// working unchanged during the migration.
exception TelegramApiException of ApiResponseError
    with
        override this.Message =
            $"Telegram API error {this.Data0.ErrorCode}: {this.Data0.Description}"

/// Thin seam over Funogram so services depend on an injectable interface (Funogram
/// itself is function-based). Build requests with Funogram.Telegram.Req.X.Make(...).
type ITelegramApi =
    /// Result-based call — for call sites that branch on failure.
    abstract Call<'a> : Funogram.Types.IRequestBase<'a> -> Task<Result<'a, ApiResponseError>>
    /// Exception-based call — drop-in for old Telegram.Bot call sites (throws TelegramApiException).
    abstract CallExn<'a> : Funogram.Types.IRequestBase<'a> -> Task<'a>
    /// Fire-and-forget call — logs API errors at Warning and swallows them (opt-in).
    abstract CallIgnore<'a> : Funogram.Types.IRequestBase<'a> -> Task<unit>
    /// Downloads a file by its getFile path: GET {origin}/file/bot{token}/{filePath}.
    abstract DownloadFile : filePath: string -> Task<byte[]>

type TelegramApi(config: BotConfig, logger: ILogger<TelegramApi>) =
    // "https://api.telegram.org/bot" -> "https://api.telegram.org/file/bot"
    // (same shape for the FakeTgApi override, whose route is GET /file/bot{token}/{*path})
    let fileBaseUrl =
        let api = (string config.ApiEndpointUrl).TrimEnd('/')
        if not (api.EndsWith "/bot") then
            failwith $"ApiEndpointUrl must end with '/bot' (got {api}) — cannot derive the file download URL"
        api.Substring(0, api.Length - "bot".Length) + "file/bot"

    interface ITelegramApi with
        member _.Call req =
            Funogram.Api.api config req |> Async.StartAsTask

        member _.CallExn req =
            task {
                match! Funogram.Api.api config req |> Async.StartAsTask with
                | Ok v -> return v
                | Error e -> return raise (TelegramApiException e)
            }

        member _.CallIgnore req =
            task {
                match! Funogram.Api.api config req |> Async.StartAsTask with
                | Ok _ -> ()
                | Error e ->
                    logger.LogWarning("Telegram API {Method} failed: {Code} {Description}",
                                      req.GetType().Name, e.ErrorCode, e.Description)
            }

        member _.DownloadFile filePath =
            config.Client.GetByteArrayAsync $"{fileBaseUrl}{config.Token}/{filePath}"

/// Funogram-flavored helpers shared by migrated bots.
[<AutoOpen>]
module TelegramApiHelpers =
    type Funogram.Telegram.Types.Update with
        /// The edited message if this update is an edit, else the new message.
        member u.EditedOrMessage : Funogram.Telegram.Types.Message option =
            u.EditedMessage |> Option.orElse u.Message
