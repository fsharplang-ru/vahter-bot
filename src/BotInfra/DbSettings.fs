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

    /// Inserts or updates a single bot_setting value. The service role can
    /// INSERT/UPDATE but not DELETE, so "removals" are value rewrites, not row deletes.
    let upsertBotSetting (connString: string) (key: string) (value: string) (typ: string) (featureGroup: string) : Task<unit> =
        task {
            use conn = new NpgsqlConnection(connString)
            let! _ = conn.ExecuteAsync(
                """
INSERT INTO bot_setting(key, value, type, feature_group, updated_at)
VALUES(@key, @value, @typ, @group, NOW())
ON CONFLICT (key) DO UPDATE SET value = @value, updated_at = NOW()
                """,
                {| key = key; value = value; typ = typ; group = featureGroup |})
            return ()
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
