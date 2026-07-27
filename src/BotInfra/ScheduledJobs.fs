namespace BotInfra

open System
open System.Threading
open System.Threading.Tasks
open Dapper
open Npgsql
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

/// Distributed scheduled-job locking + a generic hosted-service loop that ticks
/// registered jobs, each gated by a once-a-day Postgres row lease (an
/// `UPDATE ... RETURNING` atomic-acquire against a per-bot `scheduled_job` table — the
/// consuming bot owns that migration; BotInfra never creates it). Originally built for
/// AlitaBot's nightly dossier/digest jobs (Slice 5b/8, `AlitaBot.Services.ScheduledJobs`
/// — see `git log` on that file for the pre-promotion history) and promoted here once a
/// second consumer needed it. Promoting it redeploys every bot on `BotInfra` (VahterBanBot,
/// CouponHubBot) even though only AlitaBot registers jobs today — those bots simply never
/// construct a `SchedulerHostedService`.
module ScheduledJobs =

    /// Tries to acquire a scheduled job's lease for the given pod. Returns true if this
    /// pod successfully locked the job (it's due — `now` has passed today's
    /// `scheduledTime` — and no other pod currently holds the lock).
    let tryAcquire
        (connString: string)
        (timeProvider: TimeProvider)
        (jobName: string)
        (scheduledTime: TimeSpan)
        (podId: string)
        : Task<bool> =
        task {
            use conn = new NpgsqlConnection(connString)
            let now = timeProvider.GetUtcNow().UtcDateTime
            //language=postgresql
            let sql =
                """
UPDATE scheduled_job
SET locked_until = @now + INTERVAL '1 hour',
    locked_by    = @podId
WHERE job_name = @jobName
  AND @now >= (CURRENT_DATE + @scheduledTime)
  AND (last_completed_at IS NULL OR last_completed_at < (CURRENT_DATE + @scheduledTime))
  AND (locked_until IS NULL OR locked_until < @now)
RETURNING job_name;
"""
            let! result =
                conn.QueryAsync<string>(
                    sql,
                    {| jobName = jobName
                       scheduledTime = scheduledTime
                       podId = podId
                       now = now |})
            return Seq.length result > 0
        }

    /// Releases the lease and records completion time for a scheduled job.
    let complete (connString: string) (timeProvider: TimeProvider) (jobName: string) : Task =
        task {
            use conn = new NpgsqlConnection(connString)
            let now = timeProvider.GetUtcNow().UtcDateTime
            //language=postgresql
            let sql =
                """
UPDATE scheduled_job
SET last_completed_at = @now,
    locked_until      = NULL,
    locked_by         = NULL
WHERE job_name = @jobName;
"""
            let! _ = conn.ExecuteAsync(sql, {| jobName = jobName; now = now |})
            return ()
        }

    /// Records completion for a TEST_MODE-triggered run (`SchedulerHostedService.RunJobNow`),
    /// using the real system clock (`DateTime.UtcNow`) rather than the injected
    /// `TimeProvider` — consumers typically overwrite the DI `TimeProvider` with a **frozen**
    /// `FakeTimeProvider` whenever TEST_MODE is on (see AlitaBot's `Program.fs` TestMode
    /// block), and that clock is never advanced across a whole fake-suite assembly run or a
    /// real-test pod's lifetime. Writing `complete`'s `timeProvider.GetUtcNow()` there would
    /// make every TEST_MODE-triggered completion land on the exact same timestamp, so a
    /// caller polling for "`last_completed_at` advanced past a pre-request snapshot" could
    /// hang forever on any run after the first one. Only `last_completed_at` is touched —
    /// `locked_until`/`locked_by` are left alone because `RunJobNow` never goes through
    /// `tryAcquire` in the first place, so there's no lease to release.
    let completeTestTriggered (connString: string) (jobName: string) : Task =
        task {
            use conn = new NpgsqlConnection(connString)
            let now = DateTime.UtcNow
            //language=postgresql
            let sql =
                """
UPDATE scheduled_job
SET last_completed_at = @now
WHERE job_name = @jobName;
"""
            let! _ = conn.ExecuteAsync(sql, {| jobName = jobName; now = now |})
            return ()
        }

    /// A single named scheduled job registered with `SchedulerHostedService`.
    type JobDefinition =
        { /// Primary key in the `scheduled_job` table; also the name TEST_MODE's
          /// `RunJobNow`/`IsKnownJob` match against.
          Name: string
          /// The job's daily UTC time-of-day, re-evaluated on every tick (rather than
          /// captured once) so a hot-reloadable schedule setting (e.g. a `bot_setting`-backed
          /// hour) takes effect on the very next tick with no restart needed. Jobs whose
          /// schedule is compile-time-fixed can just ignore the unit and return a constant.
          ScheduledTimeUtc: unit -> TimeSpan
          /// The job's work. Errors are caught per-job — one job's failure never stops the
          /// tick loop or any other registered job.
          Run: unit -> Task }

/// Generic hosted background service driving a list of registered `ScheduledJobs.
/// JobDefinition`s. Ticks every `tickInterval`; each registered job's own
/// `ScheduledTimeUtc`/lease check (`ScheduledJobs.tryAcquire`) is what actually gates
/// whether today's run has become due, not the tick interval. Also exposes `RunJobNow`, a
/// TEST_MODE hook that runs a named job immediately, bypassing the lease/schedule check
/// entirely (consumers typically wire this to a `/test/run-job?name=` endpoint).
type SchedulerHostedService
    (
        connString: string,
        time: TimeProvider,
        jobs: ScheduledJobs.JobDefinition list,
        tickInterval: TimeSpan,
        logger: ILogger<SchedulerHostedService>
    ) =
    inherit BackgroundService()

    let podId = Environment.MachineName

    let runJob (job: ScheduledJobs.JobDefinition) =
        task {
            let! acquired = ScheduledJobs.tryAcquire connString time job.Name (job.ScheduledTimeUtc()) podId
            if acquired then
                logger.LogInformation("ScheduledJobs: acquired {Job}", job.Name)
                try
                    do! job.Run()
                    do! ScheduledJobs.complete connString time job.Name
                    logger.LogInformation("ScheduledJobs: completed {Job}", job.Name)
                with ex ->
                    logger.LogError(ex, "ScheduledJobs: {Job} failed", job.Name)
        }

    override _.ExecuteAsync(ct: CancellationToken) =
        task {
            use timer = new PeriodicTimer(tickInterval, time)
            while! timer.WaitForNextTickAsync(ct) do
                if not ct.IsCancellationRequested then
                    for job in jobs do
                        try
                            do! runJob job
                        with ex ->
                            logger.LogError(ex, "ScheduledJobs: error during tick")
        }
        :> Task

    /// TEST_MODE hook: starts a named job immediately, regardless of the lease/schedule
    /// (never acquires the lease or touches `locked_until`/`locked_by`, so it can't collide
    /// with a real pod's in-progress run), and returns as soon as it's kicked off rather than
    /// waiting for it to finish (`BotInfra.Utils.fireAndForget`) — a job can run for tens of
    /// seconds (e.g. sequential real LLM calls), and awaiting it inline would hold an HTTP
    /// response open for that whole time. Once the fire-and-forget work finishes, it does
    /// stamp `last_completed_at` (via `ScheduledJobs.completeTestTriggered`) so a caller has
    /// something to poll for — see that function's doc comment for why it can't just reuse
    /// `complete`.
    member _.RunJobNow(jobName: string) : Task =
        task {
            match jobs |> List.tryFind (fun j -> j.Name = jobName) with
            | Some job ->
                fireAndForget logger "scheduled_jobs.run_now" (fun () ->
                    task {
                        do! job.Run()
                        do! ScheduledJobs.completeTestTriggered connString job.Name
                    }
                    :> Task)
            | None -> logger.LogWarning("ScheduledJobs: unknown job {Job}", jobName)
        }

    /// Whether `jobName` is one `RunJobNow` recognizes — callers typically use this to 404
    /// unknown names up front rather than accepting the request and then just logging a
    /// warning from inside the fire-and-forget task.
    member _.IsKnownJob(jobName: string) : bool = jobs |> List.exists (fun j -> j.Name = jobName)
