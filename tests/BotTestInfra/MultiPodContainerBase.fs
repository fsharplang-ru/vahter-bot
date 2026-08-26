namespace BotTestInfra

/// Config for an N-instance fixture. Wraps BotContainerConfig unchanged (same fields drive the
/// shared network/db/fake-TG/image-build wiring as the single-pod fixture for the same bot —
/// both share the ONE cached image spec keyed by `Base.AppImageName`).
type MultiPodContainerConfig =
    { Base: BotContainerConfig
      /// Number of bot app instances to start from the same built image. Default 2.
      InstanceCount: int }

/// Thin N-instance subclass of BotContainerBase — all container-setup (network, postgres,
/// flyway, fakes, N app containers from ONE cached image spec) lives once in BotContainerBase;
/// this type only threads `InstanceCount` through to it. See BotContainerBase's own doc comment
/// for the single-pod/multi-pod compat contract.
[<AbstractClass>]
type MultiPodContainerBase(config: MultiPodContainerConfig) =
    inherit BotContainerBase(config.Base, config.InstanceCount)
