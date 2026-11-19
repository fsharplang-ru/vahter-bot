module VahterBanBot.Unit.Tests.JoinHistoryRecoveryTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open TL
open VahterBanBot
open Xunit
open BotInfra

[<Theory>]
[<InlineData("cancel")>]
[<InlineData("timeout")>]
[<InlineData("io")>]
[<InlineData("auth")>]
let ``Failed clients are disposed and the next call uses a fresh client`` kind = task {
    let mutable created = 0
    let disposed = ResizeArray<int>()
    let create () =
        created <- created + 1
        let generation = created
        let error: exn =
            match kind with
            | "cancel" -> TaskCanceledException()
            | "timeout" -> TimeoutException()
            | "auth" -> RpcException(401, "SESSION_REVOKED")
            | _ -> IOException()
        let history =
            { new IJoinHistory with
                member _.Read(_, ids) =
                    if generation = 1 then Task.FromException<MessageBase[]>(error)
                    else Task.FromResult(ids |> Array.map (fun id -> MessageEmpty(id=id) :> MessageBase))
                member _.Delete(_, _) = Task.FromResult() }
        let lifetime = { new IAsyncDisposable with member _.DisposeAsync() = disposed.Add generation; ValueTask.CompletedTask }
        history, lifetime
    use recovery = new RecoveringJoinHistory(create, NullLogger.Instance, CancellationToken.None, (fun () -> TimeSpan.FromSeconds 5.))
    let history = recovery :> IJoinHistory
    let! error = Record.ExceptionAsync(fun () -> history.Read(-1000000000001L, [|1|]) :> Task)
    Assert.NotNull(error)
    Assert.Equal<int>([|1|], disposed)
    let! result = history.Read(-1000000000001L, [|1|])
    %Assert.IsType<MessageEmpty>(Assert.Single(result))
    Assert.Equal(2, created)
}

[<Fact>]
let ``Flood wait keeps the client for a later retry`` () = task {
    let mutable created, calls, disposed = 0, 0, 0
    let create () =
        created <- created+1
        let history =
            { new IJoinHistory with
                member _.Read(_, _) = Task.FromResult(Array.empty<MessageBase>)
                member _.Delete(_, _) =
                    calls <- calls+1
                    if calls = 1 then Task.FromException<unit>(RpcException(420,"FLOOD_WAIT",20))
                    else Task.FromResult() }
        history, { new IAsyncDisposable with member _.DisposeAsync() = disposed <- disposed+1; ValueTask.CompletedTask }
    use recovery = new RecoveringJoinHistory(create, NullLogger.Instance, CancellationToken.None, (fun () -> TimeSpan.FromSeconds 5.))
    let history = recovery :> IJoinHistory
    let! _ = Assert.ThrowsAsync<RpcException>(fun () -> history.Delete(-1000000000001L,[|1|]))
    Assert.Equal(0,disposed)
    do! history.Delete(-1000000000001L,[|1|])
    Assert.Equal(1,created)
}

[<Fact>]
let ``A stalled delete is bounded and disposes the client before another call`` () = task {
    let mutable disposed = false
    let stalled = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let create () =
        let history =
            { new IJoinHistory with
                member _.Read(_, _) = Task.FromResult(Array.empty<MessageBase>)
                member _.Delete(_, _) = stalled.Task }
        history, { new IAsyncDisposable with member _.DisposeAsync() = disposed <- true; %stalled.TrySetCanceled(); ValueTask.CompletedTask }
    use recovery = new RecoveringJoinHistory(create, NullLogger.Instance, CancellationToken.None, (fun () -> TimeSpan.FromMilliseconds 100.))
    let! _ = Assert.ThrowsAsync<TimeoutException>(fun () -> (recovery :> IJoinHistory).Delete(-1000000000001L,[|1|]))
    Assert.True(disposed)
}

[<Fact>]
let ``Shutdown cancels a pending read without creating another client`` () = task {
    use ct = new CancellationTokenSource()
    let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable created, disposed = 0, 0
    let create () =
        created <- created+1
        let pending = TaskCompletionSource<MessageBase[]>(TaskCreationOptions.RunContinuationsAsynchronously)
        let history =
            { new IJoinHistory with
                member _.Read(_, _) = entered.SetResult(); pending.Task
                member _.Delete(_, _) = Task.FromResult() }
        history, { new IAsyncDisposable with member _.DisposeAsync() = disposed <- disposed+1; %pending.TrySetCanceled(); ValueTask.CompletedTask }
    use recovery = new RecoveringJoinHistory(create, NullLogger.Instance, ct.Token, (fun () -> TimeSpan.FromMinutes 1.))
    let read = (recovery :> IJoinHistory).Read(-1000000000001L,[|1|])
    do! entered.Task.WaitAsync(TimeSpan.FromSeconds 5.)
    ct.Cancel()
    let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> read)
    let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> (recovery :> IJoinHistory).Read(-1000000000001L,[|1|]))
    Assert.Equal(1,created)
    Assert.Equal(1,disposed)
}
