namespace BotInfra

open System
open System.Collections.Generic
open System.Threading.Tasks
open Dapper
open Npgsql

/// Shared bot_settings table access.
module DbSettings =

    let private readSettings (conn: NpgsqlConnection) = task {
        let! rows = conn.QueryAsync<{| key: string; value: string |}>(
            "SELECT key, value FROM bot_setting WHERE value IS NOT NULL")
        return rows |> Seq.map (fun r -> r.key, r.value) |> readOnlyDict
    }

    let loadBotSettings (connString: string) = task {
        use conn = new NpgsqlConnection(connString)
        return! readSettings conn
    }

    let pooledConnectionString minimumRequired (settings: IReadOnlyDictionary<string,string>) (connectionString: string) =
        let config = NpgsqlConnectionStringBuilder(connectionString)
        let key = "DB_MAX_POOL_SIZE"
        let limit =
            match settings.TryGetValue key with
            | true, raw ->
                match Int32.TryParse raw with
                | true, n when n >= minimumRequired -> min config.MaxPoolSize n
                | _ -> invalidArg key $"Expected an integer of at least {minimumRequired}"
            | _ -> invalidOp $"Missing required bot_setting: {key}"
        if limit < minimumRequired then
            invalidOp $"DATABASE_URL Maximum Pool Size must be at least {minimumRequired}"
        config.Pooling <- true
        config.MinPoolSize <- min config.MinPoolSize limit
        config.MaxPoolSize <- limit
        config.ConnectionString

    let loadStartupSettings minimumRequired (connectionString: string) = task {
        // Dispose the single-connection bootstrap pool before any runtime consumer starts.
        let bootstrap = NpgsqlConnectionStringBuilder(connectionString)
        bootstrap.Pooling <- true
        bootstrap.MinPoolSize <- 0
        bootstrap.MaxPoolSize <- 1
        use source = NpgsqlDataSource.Create(bootstrap.ConnectionString)
        use! conn = source.OpenConnectionAsync()
        let! settings = readSettings conn
        return pooledConnectionString minimumRequired settings connectionString, settings
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
