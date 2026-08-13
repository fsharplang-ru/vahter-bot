/// Focused unit coverage for VahterBanBot.TtlCache — the in-process cache backing
/// /vahter_report's DB-query abuse bound. Substitutes for an integration-level "two
/// invocations produce one DB query" assertion (see the brief's item 5): DbService has no
/// query-count instrumentation to hook into without adding test-only surface to production
/// code, whereas TtlCache's own `compute` callback is trivially countable.
module VahterBanBot.Unit.Tests.TtlCacheTests

open System
open System.Threading.Tasks
open VahterBanBot.TtlCache
open Xunit

[<Fact>]
let ``two GetOrCompute calls for the same key within the TTL run compute only once`` () = task {
    let now = DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero)
    let cache = TtlCache<int64, int>(fun () -> now)
    let mutable computeCalls = 0
    let compute () = task {
        computeCalls <- computeCalls + 1
        return 42
    }

    let! first = cache.GetOrCompute(1L, TimeSpan.FromMinutes 5.0, compute)
    let! second = cache.GetOrCompute(1L, TimeSpan.FromMinutes 5.0, compute)

    Assert.Equal(42, first)
    Assert.Equal(42, second)
    Assert.Equal(1, computeCalls)
}

[<Fact>]
let ``a call after the TTL expires re-runs compute`` () = task {
    let mutable now = DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero)
    let cache = TtlCache<int64, int>(fun () -> now)
    let mutable computeCalls = 0
    let compute () = task {
        computeCalls <- computeCalls + 1
        return computeCalls
    }

    let! first = cache.GetOrCompute(1L, TimeSpan.FromMinutes 5.0, compute)
    now <- now.AddMinutes 5.0001
    let! second = cache.GetOrCompute(1L, TimeSpan.FromMinutes 5.0, compute)

    Assert.Equal(1, first)
    Assert.Equal(2, second)
    Assert.Equal(2, computeCalls)
}

[<Fact>]
let ``different keys are cached independently`` () = task {
    let now = DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero)
    let cache = TtlCache<int64, int>(fun () -> now)
    let mutable computeCalls = 0
    let compute (v: int) () = task {
        computeCalls <- computeCalls + 1
        return v
    }

    let! chatA = cache.GetOrCompute(-666L, TimeSpan.FromMinutes 5.0, compute 1)
    let! chatB = cache.GetOrCompute(-42L, TimeSpan.FromMinutes 5.0, compute 2)

    Assert.Equal(1, chatA)
    Assert.Equal(2, chatB)
    Assert.Equal(2, computeCalls)
}
