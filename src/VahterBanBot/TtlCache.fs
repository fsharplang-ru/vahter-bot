module VahterBanBot.TtlCache

open System
open System.Collections.Concurrent
open System.Threading.Tasks

/// Minimal in-process, per-key TTL cache. Built for /vahter_report's abuse bound (the bot has
/// no rate limiting and this is its first public, non-vahter-gated command) but deliberately
/// generic/reusable. Single bot replica in prod, so no distributed-cache concerns; a stale
/// entry is simply replaced on the next miss — no background eviction loop, which is fine at
/// this bot's scale (~34 monitored chats).
/// `now` is injected (rather than reading DateTimeOffset.UtcNow directly) purely so this class
/// is unit-testable without wall-clock sleeps. `ttl` is a per-call argument (not fixed at
/// construction) so a caller backed by hot-reloadable config — like
/// BotConfiguration.ReportCacheTtlSeconds — picks up a changed TTL immediately, without
/// recreating the cache (and losing already-cached entries) on every /reload-settings.
type TtlCache<'k, 'v when 'k: equality>(now: unit -> DateTimeOffset) =
    let store = ConcurrentDictionary<'k, DateTimeOffset * 'v>()

    /// Returns the cached value for `key` if it was stored within `ttl`; otherwise calls
    /// `compute`, caches the result, and returns it. Not linearizable under concurrent misses
    /// for the same key (two callers can both miss and both call `compute`) — acceptable here
    /// since `compute` is idempotent (a read-only stats query) and the bot is a single replica.
    member _.GetOrCompute(key: 'k, ttl: TimeSpan, compute: unit -> Task<'v>) : Task<'v> =
        task {
            match store.TryGetValue key with
            | true, (cachedAt, value) when now () - cachedAt < ttl -> return value
            | _ ->
                let! value = compute ()
                store.[key] <- (now (), value)
                return value
        }
