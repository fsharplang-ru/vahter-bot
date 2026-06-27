namespace BotInfra

open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Serialization

/// Shared JSON helpers for bot configuration.
module JsonSetup =
    /// JSON options suitable for deserializing config values (Web defaults + AllowReadingFromString).
    let configJsonOptions =
        let opts = JsonSerializerOptions(JsonSerializerDefaults.Web)
        opts.NumberHandling <- JsonNumberHandling.AllowReadingFromString
        opts

    /// Deserialize a JSON string using configJsonOptions.
    let fromJson<'a> (json: string) =
        JsonSerializer.Deserialize<'a>(json, configJsonOptions)

    /// Serialize a value using configJsonOptions.
    let toJson<'a> (value: 'a) =
        JsonSerializer.Serialize<'a>(value, configJsonOptions)

    /// Add or overwrite a name->id entry in a JSON dict blob. The key has any
    /// leading '@' trimmed. Returns the new JSON string.
    let addEntry (currentJson: string) (key: string) (id: int64) =
        let d = fromJson<Dictionary<string, int64>> currentJson
        d[key.TrimStart('@')] <- id
        toJson d

    /// Remove every entry whose value equals the given id. Returns the new JSON string.
    let removeEntryById (currentJson: string) (id: int64) =
        let d = fromJson<Dictionary<string, int64>> currentJson
        let keys = d |> Seq.filter (fun kv -> kv.Value = id) |> Seq.map (fun kv -> kv.Key) |> Seq.toList
        for k in keys do d.Remove k |> ignore
        toJson d

    /// Remove the entry matching the given key (leading '@' trimmed). Returns the new JSON string.
    let removeEntryByKey (currentJson: string) (key: string) =
        let d = fromJson<Dictionary<string, int64>> currentJson
        d.Remove(key.TrimStart('@')) |> ignore
        toJson d
