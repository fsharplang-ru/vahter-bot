namespace BotInfra

open Microsoft.Extensions.Options

/// IOptions<'T> backed by a mutable slot. Register as a singleton keyed by
/// IOptions<'T>; DI consumers read `.Value` on each access. `/reload-settings`
/// handlers call `Set` to publish a new value without a pod restart.
///
/// `.Value` and `.Set` are exposed as plain members so Program.fs can use them
/// directly without `:> IOptions<_>` casts.
type LiveOptions<'T when 'T : not struct>(initial: 'T) =
    let mutable current = initial
    member _.Value = current
    member _.Set(value: 'T) = current <- value
    interface IOptions<'T> with
        member _.Value = current
