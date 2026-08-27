module VahterBanBot.Tests.UpdateChatAdminsTests

open System
open VahterBanBot
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Npgsql
open Dapper
open Xunit

/// Postgres-backed chat_admin table (V46) + interval lease integration tests. Uses `DbService`
/// directly, not the app HTTP surface -- there is no HTTP endpoint for this internal mechanism.
type UpdateChatAdminsTests(fixture: MlEnabledVahterTestContainers, _unused: MlAwaitFixture) =

    /// "One pod fetches, all pods read" -- SaveChatAdmins fully replaces the table (delete-then-
    /// insert); GetChatAdminIds reflects it, including removal of a stale admin, not just accumulation.
    [<Fact>]
    let ``SaveChatAdmins replaces table contents; GetChatAdminIds reflects it (shared across pods)`` () = task {
        let db = DbService(fixture.DbConnectionString, TimeProvider.System)
        let chatA, chatB = -9001L, -9002L
        let userX, userY = 555001L, 555002L

        do! db.SaveChatAdmins([| chatA, userX; chatB, userY |])
        let! idsFirst = db.GetChatAdminIds()
        Assert.Contains(userX, idsFirst)
        Assert.Contains(userY, idsFirst)

        do! db.SaveChatAdmins([| chatA, userX |])
        let! idsSecond = db.GetChatAdminIds()
        Assert.Contains(userX, idsSecond)
        Assert.DoesNotContain(userY, idsSecond)
    }

    /// Uses a dedicated job_name (not 'chat_admins_refresh') so this doesn't race the live
    /// UpdateChatAdmins hosted service already ticking against the shared container.
    [<Fact>]
    let ``TryAcquireIntervalJob: one pod wins the lease, a second is blocked until minInterval elapses`` () = task {
        let jobName = $"test_interval_job_{Guid.NewGuid():N}"
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        let! _ = conn.ExecuteAsync("INSERT INTO scheduled_job (job_name) VALUES (@jobName)", {| jobName = jobName |})

        let db = DbService(fixture.DbConnectionString, TimeProvider.System)
        let minInterval = TimeSpan.FromHours 1.0

        let! podAAcquired = db.TryAcquireIntervalJob(jobName, minInterval, "pod-a")
        Assert.True(podAAcquired, "First pod should acquire the never-run lease")

        let! podBAcquired = db.TryAcquireIntervalJob(jobName, minInterval, "pod-b")
        Assert.False(podBAcquired, "A second pod must not acquire the lease while pod-a holds it")

        do! db.CompleteScheduledJob(jobName)

        let! podBRightAfterComplete = db.TryAcquireIntervalJob(jobName, minInterval, "pod-b")
        Assert.False(podBRightAfterComplete, "Within minInterval of completion, no pod should re-acquire")

        // Backdate completion past minInterval instead of sleeping for real.
        let! _ =
            conn.ExecuteAsync(
                "UPDATE scheduled_job SET last_completed_at = last_completed_at - make_interval(mins => 61) WHERE job_name = @jobName",
                {| jobName = jobName |})
        let! podBAfterIntervalElapsed = db.TryAcquireIntervalJob(jobName, minInterval, "pod-b")
        Assert.True(podBAfterIntervalElapsed, "After minInterval elapses, the lease should be acquirable again")
    }

    interface IClassFixture<MlAwaitFixture>
