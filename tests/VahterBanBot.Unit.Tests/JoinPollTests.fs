module VahterBanBot.Unit.Tests.JoinPollTests

open System
open System.Threading
open System.Threading.Tasks
open BotInfra
open BotInfra.JsonSetup
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open VahterBanBot
open VahterBanBot.Types
open Xunit

[<Theory>]
[<InlineData(16, 16, 100, 32, 0)>]
[<InlineData(64, 64, 100, 100, 0)>]
[<InlineData(50, 50, 50, 50, 0)>]
[<InlineData(16, 8, 100, 16, 500)>]
[<InlineData(32, 3, 100, 16, 1000)>]
[<InlineData(100, 0, 100, 50, 1000)>]
[<InlineData(5, 0, 5, 5, 1000)>]
let ``Claim size and poll delay follow ready work`` limit count cap next delay =
    Assert.Equal<int * int>((next, delay), JoinDeletions.nextPoll limit count cap)

[<Fact>]
let ``Stopping a deletion worker during database error backoff completes normally`` () = task {
    let failure = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let logger =
        { new ILogger<JoinDeletionWorker> with
            member _.BeginScope _ = null
            member _.IsEnabled _ = true
            member _.Log(_, _, _, ex, _) = if not (isNull ex) then %failure.TrySetResult() }
    let config = { fromJson<BotConfiguration> "{}" with JoinDeleteMaxClaim = 16 }
    let queue = JoinDeletionQueue("Host=127.0.0.1;Port=1;Username=test;Database=test;Timeout=1")
    use worker = new JoinDeletionWorker(queue, Unchecked.defaultof<ITelegramApi>, Options.Create(config), logger)
    use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 5.)
    do! worker.StartAsync(timeout.Token)
    do! failure.Task.WaitAsync(timeout.Token)
    do! worker.StopAsync(timeout.Token)
    do! worker.ExecuteTask.WaitAsync(timeout.Token)
}
