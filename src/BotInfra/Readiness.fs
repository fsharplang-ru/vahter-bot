namespace BotInfra

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Npgsql

/// Generic cached boolean check: `compute` runs at most once per `ttl`; both success and
/// failure are cached. `timeProvider` is injected for testability (BOT_FIXED_UTC_NOW /
/// BotInfra.Time.MutableTimeProvider).
type CachedBoolCheck(timeProvider: TimeProvider, ttl: TimeSpan, compute: unit -> Task<bool>) =
    // Reference assignment is atomic; a concurrent cache miss can double-compute, which is
    // harmless here (both callers idempotent, cheap query) — same trade-off as VahterBanBot.TtlCache.
    let mutable cached: (DateTimeOffset * bool) option = None

    /// Returns the cached result if within `ttl`; otherwise recomputes and caches.
    member _.CheckAsync() : Task<bool> =
        task {
            let now = timeProvider.GetUtcNow()
            match cached with
            | Some(cachedAt, result) when now - cachedAt < ttl -> return result
            | _ ->
                let! result = compute ()
                cached <- Some(now, result)
                return result
        }

/// Caches a `SELECT 1` DB ping (success or failure) so a period-`cacheTtl` probe costs at
/// most one round-trip per period. `pingTimeout` bounds the query so it can never exceed the
/// probe's own HTTP timeout.
type DbPingCheck(connString: string, timeProvider: TimeProvider, ?cacheTtl: TimeSpan, ?pingTimeout: TimeSpan) =
    let cacheTtl = defaultArg cacheTtl (TimeSpan.FromSeconds 5.0)
    let pingTimeout = defaultArg pingTimeout (TimeSpan.FromSeconds 2.0)

    let ping () : Task<bool> =
        task {
            try
                use conn = new NpgsqlConnection(connString)
                use cts = new CancellationTokenSource(pingTimeout)
                do! conn.OpenAsync(cts.Token)
                use cmd = new NpgsqlCommand("SELECT 1", conn)
                let! _ = cmd.ExecuteScalarAsync(cts.Token)
                return true
            with _ ->
                return false
        }

    let cache = CachedBoolCheck(timeProvider, cacheTtl, ping)

    member _.CheckAsync() : Task<bool> = cache.CheckAsync()

/// Shared GET /ready mapping used by all bots.
module Readiness =

    /// A named readiness check: true means healthy.
    type Check = string * (unit -> Task<bool>)

    let private runCheck (name: string, check: unit -> Task<bool>) : Task<string option> =
        task {
            try
                let! ok = check ()
                return if ok then None else Some name
            with _ ->
                return Some name
        }

    /// Maps GET /ready -> 200 "READY" iff every check passes, else 503 naming the failures.
    let mapReadyEndpoint (checks: Check list) (app: WebApplication) =
        %app.MapGet("/ready", Func<Task<IResult>>(fun () ->
            task {
                let! results = checks |> List.map runCheck |> Task.WhenAll
                let failed = results |> Array.choose id
                if failed.Length = 0 then
                    return Results.Text "READY"
                else
                    return Results.Text(sprintf "NOT READY: %s" (String.Join(", ", failed)), statusCode = 503)
            }))
