namespace BotInfra

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

/// Cross-pod settings propagation via Postgres LISTEN/NOTIFY — one channel name per bot (own DB).
/// No payload; every listener just re-runs its own `reloadSettings` and re-reads `bot_setting`.
module SettingsNotify =

    [<Literal>]
    let Channel = "bot_settings_changed"

    /// Fires `NOTIFY bot_settings_changed`. Callers run this after their own `reloadSettings()`
    /// already applied the change, so reloading twice (local + own notification) is harmless.
    let notifySettingsChanged (connString: string) : Task =
        task {
            use conn = new NpgsqlConnection(connString)
            do! conn.OpenAsync()
            use cmd = new NpgsqlCommand($"NOTIFY {Channel}", conn)
            let! _ = cmd.ExecuteNonQueryAsync()
            ()
        }
        :> Task

/// Reloads on every (re)connect (closes the drop→NOTIFY window) and on each NOTIFY. Retries
/// connection loss with capped exponential backoff; up/down transition logged once, not per retry.
type SettingsListenerHostedService
    (
        connString: string,
        reload: unit -> Task,
        logger: ILogger<SettingsListenerHostedService>,
        ?minBackoff: TimeSpan,
        ?maxBackoff: TimeSpan
    ) =
    inherit BackgroundService()

    let minBackoff = defaultArg minBackoff (TimeSpan.FromSeconds 1.0)
    let maxBackoff = defaultArg maxBackoff (TimeSpan.FromSeconds 30.0)

    /// `wasDown` selects the reconnect-vs-initial-connect log wording.
    let runOnce (ct: CancellationToken) (wasDown: bool) : Task =
        task {
            use conn = new NpgsqlConnection(connString)
            do! conn.OpenAsync(ct)
            use listenCmd = new NpgsqlCommand($"LISTEN {SettingsNotify.Channel}", conn)
            let! _ = listenCmd.ExecuteNonQueryAsync(ct)
            if wasDown then
                logger.LogInformation("SettingsListener: reconnected, listening on {Channel}", SettingsNotify.Channel)
            else
                logger.LogInformation("SettingsListener: listening on {Channel}", SettingsNotify.Channel)
            // Closes the missed-notification window: a NOTIFY sent while this pod was
            // disconnected would otherwise never be observed.
            do! reload()
            while not ct.IsCancellationRequested do
                do! conn.WaitAsync(ct)
                do! reload()
        }
        :> Task

    override _.ExecuteAsync(ct: CancellationToken) =
        task {
            let mutable backoff = minBackoff
            let mutable isDown = false
            while not ct.IsCancellationRequested do
                try
                    do! runOnce ct isDown
                    isDown <- false
                    backoff <- minBackoff
                with
                | :? OperationCanceledException -> ()
                | ex when not ct.IsCancellationRequested ->
                    if not isDown then
                        logger.LogWarning(ex, "SettingsListener: connection lost, reconnecting with backoff")
                        isDown <- true
                    do! Task.Delay(backoff, ct)
                    backoff <- TimeSpan.FromSeconds(min maxBackoff.TotalSeconds (backoff.TotalSeconds * 2.0))
        }
        :> Task
