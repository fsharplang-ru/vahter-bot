module VahterBanBot.Telemetry

open System.Diagnostics

/// Shared ActivitySource for all VahterBanBot tracing.
/// Single instance ensures all spans share the same source registration in OTEL,
/// and ActivitySource.StartActivity() picks up Activity.Current as parent —
/// so fire-and-forget spans started after Task.Run will still appear as children
/// of the webhook trace via ExecutionContext propagation.
let botActivity = new ActivitySource("VahterBanBot")
