namespace BotInfra

open System.Collections.Generic
open System.Threading.Tasks
open Dapper
open Npgsql

/// Shared bot_settings table access.
module DbSettings =

    /// Loads all key-value pairs from the bot_setting table.
    let loadBotSettings (connString: string) : Task<IReadOnlyDictionary<string, string>> =
        task {
            use conn = new NpgsqlConnection(connString)
            let! rows = conn.QueryAsync<{| key: string; value: string |}>(
                "SELECT key, value FROM bot_setting WHERE value IS NOT NULL")
            return rows |> Seq.map (fun r -> r.key, r.value) |> readOnlyDict
        }

    type BotSettingsAccessor(settings: IReadOnlyDictionary<string, string>) =
        member _.GetSetting(key: string) =
            match settings.TryGetValue key with
            | true, v -> v
            | _ -> null

        member this.GetSettingOr(key: string, defaultValue: string) =
            match this.GetSetting key with
            | null -> defaultValue
            | v -> v

        member this.GetRequiredSetting(key: string) =
            match this.GetSetting key with
            | null -> failwithf "Required setting '%s' not found in bot_setting table" key
            | v -> v
