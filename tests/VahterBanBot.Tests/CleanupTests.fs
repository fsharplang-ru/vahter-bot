module CleanupTests

open System
open VahterBanBot.Tests.ContainerTestBase
open Xunit

type CleanupTests(fixture: MlDisabledVahterTestContainers) =
    [<Fact>]
    let ``Orphaned callbacks get expired by cleanup`` () = task {
        let callbackId = Guid.NewGuid()

        // insert an orphaned CallbackCreated event (10 days old)
        do! fixture.InsertOrphanedCallback(callbackId, 10)

        // verify no CallbackExpired yet
        let! expired = fixture.HasCallbackExpired(callbackId)
        Assert.False(expired, "should not be expired before cleanup")

        // run cleanup for events older than 1 day
        let! cleaned = fixture.CleanupOrphanedCallbacks(TimeSpan.FromDays 1)
        Assert.True(cleaned >= 1, $"expected at least 1 orphaned callback expired, got {cleaned}")

        // verify CallbackExpired was inserted
        let! expired = fixture.HasCallbackExpired(callbackId)
        Assert.True(expired, "should be expired after cleanup")
    }

    [<Fact>]
    let ``Recent callbacks are not cleaned up`` () = task {
        let callbackId = Guid.NewGuid()

        // insert a CallbackCreated event that is NOT old enough (0 days old = just now)
        do! fixture.InsertOrphanedCallback(callbackId, 0)

        // run cleanup for events older than 7 days
        let! _ = fixture.CleanupOrphanedCallbacks(TimeSpan.FromDays 7)

        // verify NOT expired (too recent)
        let! expired = fixture.HasCallbackExpired(callbackId)
        Assert.False(expired, "recent callback should not be cleaned up")
    }
