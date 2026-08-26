namespace BotInfra

open System
open System.Collections.Generic
open System.Text.Json
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.FSharp.Reflection

/// Backs GET /config-dump (both bots, prod endpoint) — reflects the live effective
/// BotConfiguration record, redacting any field whose name marks it secret-bearing so the
/// endpoint can never leak a token/key/connection-string value, only its presence.
module SettingsDump =
    let private secretMarkers = [ "token"; "key"; "password"; "secret"; "connectionstring" ]

    let private isSecretField (name: string) =
        let lowered = name.ToLowerInvariant()
        secretMarkers |> List.exists lowered.Contains

    /// Reflects over a record's fields; secret-marked fields become `{ present = bool }`.
    let redactedFields<'T> (config: 'T) : Dictionary<string, obj> =
        let fields = FSharpType.GetRecordFields(typeof<'T>)
        let result = Dictionary<string, obj>()
        for f in fields do
            let value = f.GetValue(config)
            if isSecretField f.Name then
                let present =
                    if isNull value then false
                    else
                        match value with
                        | :? string as s -> not (System.String.IsNullOrEmpty s)
                        | _ -> true
                result[f.Name] <- box {| present = present |}
            else
                result[f.Name] <- value
        result

    /// `jsonOpts` must be able to serialize every non-secret field's runtime type — vahter's
    /// config has DU/TimeSpan/Dictionary fields and needs its FSharp.SystemTextJson-configured
    /// `eventJsonOpts`; coupon's config is flat primitives and can pass plain default options.
    let toJson<'T> (jsonOpts: JsonSerializerOptions) (config: 'T) : string =
        JsonSerializer.Serialize(redactedFields config, jsonOpts)

    /// Shared GET /config-dump mapping used by all bots. `gate` decides auth per request
    /// (each bot passes its own admin-auth mechanism); `getConfigJson` returns the redacted
    /// JSON for the live config (bots close over `toJson`/their own jsonOpts/IOptions).
    let mapConfigDumpEndpoint (gate: HttpContext -> bool) (getConfigJson: unit -> string) (app: WebApplication) =
        %app.MapGet("/config-dump", Func<HttpContext, IResult>(fun ctx ->
            if not (gate ctx) then
                Results.Text("Access Denied", statusCode = 401)
            else
                Results.Text(getConfigJson (), "application/json")))
