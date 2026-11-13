module VahterBanBot.Tests.JoinHistoryTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Dapper
open BotInfra
open Microsoft.Extensions.Logging.Abstractions
open Npgsql
open TL
open VahterBanBot
open VahterBanBot.Tests.ContainerTestBase
open Xunit

let empty id = MessageEmpty(id=id) :> MessageBase
let service channel minute id action =
    MessageService(id=id, date=minute, peer_id=PeerChannel(channel_id=channel), action=action) :> MessageBase

type JoinHistoryTests(fixture: MlDisabledVahterTestContainers) =
    [<Fact>]
    member _.``Delayed sweep removes missed and previously accepted joins and verifies them across replicas`` () = task {
        let chat = -1000000017061L
        let channel = JoinHistory.channelId chat
        let now = DateTime.UtcNow
        let minute = DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc).AddMinutes(-5.)
        let queue = JoinDeletionQueue(fixture.DbConnectionString)
        let sweep = JoinHistorySweep(fixture.DbConnectionString, NullLogger.Instance)
        let run history = sweep.Run(history, (fun () -> [| chat |]), CancellationToken.None)
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        let args = {| chat=chat |}
        let expire () = conn.ExecuteAsync("UPDATE join_history_check SET due_at=now() WHERE chat_id=@chat", args)
        let history = Dictionary<int, MessageBase>()
        history[99] <- service channel minute 99 (MessageActionChatJoinedByLink())
        history[110] <- service channel minute 110 (MessageActionChatAddUser())
        history[132] <- service channel minute 132 (MessageActionChatJoinedByRequest())
        history[98] <- service channel minute 98 (MessageActionChatDeleteUser())
        history[105] <- Message(id=105) :> MessageBase
        history[106] <- service channel (minute.AddMinutes(-1.)) 106 (MessageActionChatAddUser())
        history[107] <- service (channel+1L) minute 107 (MessageActionChatAddUser())
        let mutable reads = 0
        let deleted = ResizeArray<int>()
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let fake =
            { new IJoinHistory with
                member _.Read(_, ids) = task {
                    reads <- reads+1
                    %entered.TrySetResult()
                    do! release.Task.WaitAsync(TimeSpan.FromSeconds 10.)
                    return ids |> Array.map (fun id -> match history.TryGetValue id with true,m -> m | _ -> empty id)
                }
                member _.Delete(actual, ids) = task {
                    Assert.Equal(chat, actual)
                    for id in ids do
                        deleted.Add id
                        %history.Remove(id)
                } }
        for id in 100..130 do do! queue.Enqueue(chat,int64 id,minute)
        do! queue.Enqueue(chat,100L,minute)
        let! _ = expire()
        do! run fake
        Assert.Equal(0, reads)
        do! queue.Enqueue(chat,131L,minute)
        do! run fake
        Assert.Equal(0, reads)
        let! _ = conn.ExecuteAsync("UPDATE join_deletion_queue SET available_at=now()+interval '1 hour', attempt=attempt+1 WHERE chat_id=@chat",args)
        let! _ = expire()
        do! run fake
        Assert.Equal(0, reads)
        let! _ = conn.ExecuteAsync("UPDATE join_deletion_queue SET available_at=NULL WHERE chat_id=@chat",args)
        let first = run fake
        do! entered.Task.WaitAsync(TimeSpan.FromSeconds 5.)
        try
            do! (run fake).WaitAsync(TimeSpan.FromSeconds 3.)
            Assert.Equal(1, reads)
        finally
            %release.TrySetResult()
        do! first
        Assert.Equal<int>([|99;110;132|],Seq.sort deleted)
        let doneReads = reads
        do! run fake
        Assert.Equal(doneReads, reads)
        Assert.True(reads >= 5)
    }

    [<Fact>]
    member _.``A surviving join after delete is retried from the same page and stops after three failures`` () = task {
        let chat = -1000000017062L
        let minute = DateTime.UtcNow.AddMinutes(-5.)
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        let args = {| chat=chat; minute=minute |}
        let! _ = conn.ExecuteAsync(
            """INSERT INTO join_history_check(chat_id,minute,notices,first_id,last_id,next_id,due_at)
               VALUES (@chat,@minute,32,40,50,40,now())""", args)
        let mutable deletes = 0
        let fake =
            { new IJoinHistory with
                member _.Read(_, ids) =
                    ids |> Array.map (function
                        | 41 -> service (JoinHistory.channelId chat) minute 41 (MessageActionChatAddUser())
                        | id -> empty id) |> Task.FromResult
                member _.Delete(_, ids) = task {
                    Assert.Equal<int>([|41|], ids)
                    deletes <- deletes+1
                } }
        let sweep = JoinHistorySweep(fixture.DbConnectionString, NullLogger.Instance)
        for _ in 1..4 do
            let! _ = conn.ExecuteAsync("UPDATE join_history_check SET due_at=now() WHERE chat_id=@chat",args)
            do! sweep.Run(fake, (fun () -> [|chat|]), CancellationToken.None)
        Assert.Equal(3,deletes)
        let! cursor = conn.ExecuteScalarAsync<int64>("SELECT next_id FROM join_history_check WHERE chat_id=@chat",args)
        Assert.Equal(40L,cursor)
    }
