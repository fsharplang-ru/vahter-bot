/// Coverage for BotInfra.Readiness (CachedBoolCheck, DbPingCheck) — the shared /ready
/// building blocks consumed by VahterBanBot and CouponHubBot. Mirrors TtlCacheTests' style:
/// CachedBoolCheck's caching is proven with a countable fake `compute`; DbPingCheck's failure
/// path is proven with a real but unreachable connection string (no live DB needed).
module VahterBanBot.Unit.Tests.ReadinessTests

open System
open System.Threading.Tasks
open BotInfra
open Xunit

[<Fact>]
let ``two CheckAsync calls within the ttl run compute only once`` () = task {
    let now = DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero)
    let mutable computeCalls = 0
    let compute () = task {
        computeCalls <- computeCalls + 1
        return true
    }
    let check = CachedBoolCheck(Time.FixedTimeProvider now, TimeSpan.FromSeconds 5.0, compute)

    let! first = check.CheckAsync()
    let! second = check.CheckAsync()

    Assert.True first
    Assert.True second
    Assert.Equal(1, computeCalls)
}

[<Fact>]
let ``a call after the ttl expires recomputes`` () = task {
    let t0 = DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero)
    let mtp = Time.MutableTimeProvider(Time.FixedTimeProvider t0)
    let mutable computeCalls = 0
    let compute () = task {
        computeCalls <- computeCalls + 1
        return computeCalls = 1
    }
    let check = CachedBoolCheck(mtp, TimeSpan.FromSeconds 5.0, compute)

    let! first = check.CheckAsync()
    mtp.SetInner(Time.FixedTimeProvider(t0.AddSeconds 5.0001))
    let! second = check.CheckAsync()

    Assert.True first
    Assert.False second
    Assert.Equal(2, computeCalls)
}

[<Fact>]
let ``a failed check stays cached as failure within the ttl`` () = task {
    let now = DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero)
    let mutable computeCalls = 0
    let compute () = task {
        computeCalls <- computeCalls + 1
        return false
    }
    let check = CachedBoolCheck(Time.FixedTimeProvider now, TimeSpan.FromSeconds 5.0, compute)

    let! first = check.CheckAsync()
    let! second = check.CheckAsync()

    Assert.False first
    Assert.False second
    Assert.Equal(1, computeCalls)
}

[<Fact>]
let ``DbPingCheck reports not-ready for an unreachable connection string`` () = task {
    // Loopback + a port nothing listens on -> fast ECONNREFUSED, no live DB required.
    let connString = "Host=127.0.0.1;Port=1;Username=none;Password=none;Database=none"
    let check = DbPingCheck(connString, TimeProvider.System, pingTimeout = TimeSpan.FromSeconds 2.0)

    let! ready = check.CheckAsync()

    Assert.False ready
}
