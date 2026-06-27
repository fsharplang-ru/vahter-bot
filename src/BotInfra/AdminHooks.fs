namespace BotInfra

open System.Threading.Tasks

/// Triggers an in-memory reload of bot settings (re-reads bot_setting and rebuilds
/// the live BotConfiguration). Implemented in the host (Program.fs) where the
/// reload closure lives, and injected into services that mutate settings.
type ISettingsReloader =
    abstract Reload: unit -> Task

/// Forces an immediate cleanup run (same logic as the scheduled cleanup job).
/// Implemented by wrapping the singleton CleanupService.
type IForcedCleanup =
    abstract Run: unit -> Task
