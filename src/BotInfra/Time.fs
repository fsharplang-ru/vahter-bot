namespace BotInfra

open System
open System.Globalization

/// Time helpers (testability via TimeProvider).
module Time =
    /// Environment variable that, when set, freezes `TimeProvider` to a constant UTC time.
    /// Format: any DateTimeOffset parseable string, recommended ISO-8601 like `2026-01-21T08:00:00Z`.
    [<Literal>]
    let FixedUtcNowEnvVar = "BOT_FIXED_UTC_NOW"

    type FixedTimeProvider(fixedUtcNow: DateTimeOffset) =
        inherit TimeProvider()
        override _.GetUtcNow() = fixedUtcNow

    let private parseFixedUtcNow (raw: string) =
        match DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal) with
        | true, dto -> dto
        | _ -> failwithf "Invalid %s value: '%s'. Expected ISO-8601 like 2026-01-21T08:00:00Z" FixedUtcNowEnvVar raw

    let fromString (raw: string) : TimeProvider =
        match raw with
        | null | "" -> TimeProvider.System
        | raw -> FixedTimeProvider(parseFixedUtcNow raw) :> TimeProvider

    let fromEnvironment () : TimeProvider =
        fromString (getEnvOr FixedUtcNowEnvVar "")

    /// A TimeProvider whose inner delegate can be swapped at runtime.
    /// Used to allow test environments to advance time after the DI container is built,
    /// e.g. when `/reload-settings` is called with a new BOT_FIXED_UTC_NOW value.
    type MutableTimeProvider(initial: TimeProvider) =
        inherit TimeProvider()
        let mutable inner: TimeProvider = initial
        /// Replace the inner TimeProvider. Thread-safe (volatile write).
        member _.SetInner(tp: TimeProvider) =
            System.Threading.Volatile.Write(&inner, tp)
        override _.GetUtcNow() =
            System.Threading.Volatile.Read(&inner).GetUtcNow()
        override _.GetTimestamp() =
            System.Threading.Volatile.Read(&inner).GetTimestamp()
