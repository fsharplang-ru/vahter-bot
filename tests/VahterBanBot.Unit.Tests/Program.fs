namespace VahterBanBot.Unit.Tests

open Xunit

// LogContext is process-global ambient state — keep the suites sequential.
[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()
