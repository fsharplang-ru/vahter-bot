namespace BotInfra

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
