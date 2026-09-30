module VahterBanBot.Tests.ModerationQualityTests

open System
open System.Text.Json
open System.Threading
open Dapper
open Npgsql
open VahterBanBot
open VahterBanBot.Types
open VahterBanBot.Tests.ContainerTestBase
open Xunit

[<CLIMutable>]
type QualityRow =
    { cohort: string; system: string; llm_model: string | null
      tp: int64; tn: int64; fp: int64; fn: int64; abstained: int64; unresolved: int64; excluded: int64 }

type ModerationQualityTests(fixture: MlDisabledVahterTestContainers) =
    let day = DateTime(2001, 2, 3, 0, 0, 0, DateTimeKind.Utc)
    let chat = -900010L
    let job = ModerationQualityHistory(fixture.DbConnectionString, TimeProvider.System)
    let add id score verdict model = task {
        do! fixture.InsertRawEvent<MessageEvent>($"message:{chat}:{id}", 1,
            MessageReceived {| chatId = chat; messageId = id; userId = id; text = Some "example"; rawMessage = "{}" |}, day.AddMinutes 1.0)
        do! fixture.InsertRawEvent<DetectionEvent>($"detection:{chat}:{id}", 1,
            MlScoredMessage {| chatId = chat; messageId = id; score = score; isSpam = score > 0.0 |}, day.AddMinutes 2.0)
        do! fixture.InsertRawEvent<DetectionEvent>($"detection:{chat}:{id}", 2,
            LlmClassified {| chatId = chat; messageId = id; verdict = verdict; reason = None; modelName = model
                             promptTokens = 1; completionTokens = 1; latencyMs = 1; promptHash = None |}, day.AddMinutes 3.0)
        if verdict = "SPAM" then
            do! fixture.InsertRawEvent<ModerationEvent>($"moderation:{chat}:{id}", 1,
                BotAutoDeleted {| chatId = chat; messageId = id; userId = id
                                  reason = LlmSpam {| score = score; modelName = defaultArg model "unknown"; reason = None; cacheScope = None |} |}, day.AddMinutes 4.0)
    }
    let rows () = task {
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        let! result = conn.QueryAsync<QualityRow>("SELECT * FROM moderation_quality_daily WHERE day = @day", {| day = day |})
        return Seq.toList result
    }

    let historyAt today =
        let clock = { new TimeProvider() with override _.GetUtcNow() = DateTimeOffset(today) }
        ModerationQualityHistory(fixture.DbConnectionString, clock)
    let scoredAt at id score = task {
        do! fixture.InsertRawEvent<MessageEvent>($"message:{chat}:{id}", 1,
            MessageReceived {| chatId = chat; messageId = id; userId = id; text = Some "example"; rawMessage = "{}" |}, at)
        do! fixture.InsertRawEvent<DetectionEvent>($"detection:{chat}:{id}", 1,
            MlScoredMessage {| chatId = chat; messageId = id; score = score; isSpam = score > 0.0 |}, at.AddMinutes 1.0)
    }
    let mlAt at = task {
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        return! conn.QuerySingleAsync<QualityRow>("SELECT * FROM moderation_quality_daily WHERE day = @day AND cohort = 'all_scored' AND system = 'ml'", {| day = at |})
    }

    [<Fact>]
    member _.``Historical counts compare identical messages and replace corrected days`` () = task {
        do! add 91001L 0.7 "SPAM" (Some "sol")
        do! add 91002L -0.2 "SPAM" (Some "sol")
        do! add 91003L 0.0 "NOT_SPAM" (Some "sol")
        do! add 91004L 0.4 "NOT_SPAM" (Some "sol")
        do! add 91005L 0.5 "SKIP" (Some "sol")
        do! add 91006L 0.7 "SPAM" (Some "mini")
        do! fixture.InsertRawEvent<MessageEvent>($"message:{chat}:91006", 2,
            MessageMarkedHam {| chatId = chat; messageId = 91006L; text = "example"; markedBy = Some 1L |}, day.AddDays 2.0)
        let! completed = job.Run(QualityHistoryMode.Rebuild day, CancellationToken.None)
        Assert.Equal(1, completed)
        let! first = rows ()
        let row cohort system model = first |> List.find (fun r -> r.cohort = cohort && r.system = system && r.llm_model = model)
        let ml = row "llm_triaged" "ml" "sol"
        Assert.Equal((1L, 1L, 1L, 1L, 1L), (ml.tp, ml.tn, ml.fp, ml.fn, ml.abstained))
        let llm = row "llm_triaged" "llm" "sol"
        Assert.Equal((2L, 2L, 0L, 0L, 1L), (llm.tp, llm.tn, llm.fp, llm.fn, llm.abstained))
        Assert.Equal(1L, (row "llm_triaged" "llm" "mini").fp)
        let pipeline = row "all_scored" "pipeline" null
        Assert.Equal((2L, 2L, 1L, 0L, 1L), (pipeline.tp, pipeline.tn, pipeline.fp, pipeline.fn, pipeline.unresolved))
        do! fixture.InsertRawEvent<MessageEvent>($"message:{chat}:91006", 3,
            MessageMarkedSpam {| chatId = chat; messageId = 91006L; markedBy = Some 1L |}, day.AddDays 20.0)
        let! _ = job.Run(QualityHistoryMode.Rebuild day, CancellationToken.None)
        let! second = rows ()
        let mini = second |> List.find (fun r -> r.cohort = "llm_triaged" && r.system = "llm" && r.llm_model = "mini")
        Assert.Equal((1L, 0L), (mini.tp, mini.fp))
        let! _ = job.Run(QualityHistoryMode.Rebuild day, CancellationToken.None)
        let! third = rows ()
        Assert.Equal<QualityRow list>(List.sortBy (fun r -> r.cohort, r.system, r.llm_model) second,
                                     List.sortBy (fun r -> r.cohort, r.system, r.llm_model) third)
    }

    [<Fact>]
    member _.``Unban corrects its ban target and empty days are persisted`` () = task {
        let targetDay = day.AddDays 1.0
        let id = 92001L
        do! fixture.InsertRawEvent<MessageEvent>($"message:{chat}:{id}", 1,
            MessageReceived {| chatId = chat; messageId = id; userId = id; text = Some "example"; rawMessage = "{}" |}, targetDay)
        do! fixture.InsertRawEvent<DetectionEvent>($"detection:{chat}:{id}", 1,
            MlScoredMessage {| chatId = chat; messageId = id; score = 3.0; isSpam = true |}, targetDay.AddMinutes 1.0)
        do! fixture.InsertRawEvent<ModerationEvent>($"moderation:{chat}:{id}", 1,
            BotAutoDeleted {| chatId = chat; messageId = id; userId = id; reason = MlSpam {| score = 3.0 |} |}, targetDay.AddMinutes 2.0)
        do! fixture.InsertRawEvent<UserEvent>($"user:{id}", 1,
            UserBanned {| userId = id; bannedBy = None; actor = Some Actor.ML; chatId = Some chat; messageId = Some id
                          messageText = None; bannedAt = targetDay.AddMinutes 2.0 |}, targetDay.AddMinutes 2.0)
        let! _ = job.Run(QualityHistoryMode.Rebuild targetDay, CancellationToken.None)
        do! fixture.InsertRawEvent<UserEvent>($"user:{id}", 2,
            UserUnbanned {| userId = id; unbannedBy = Some 1L; actor = Some (Actor.User {| userId = 1L; username = None |}) |}, targetDay.AddDays 3.0)
        let! _ = job.Run(QualityHistoryMode.Daily, CancellationToken.None)
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        let! fp = conn.QuerySingleAsync<int64>("SELECT fp FROM moderation_quality_daily WHERE day = @day AND system = 'pipeline'", {| day = targetDay |})
        Assert.Equal(1L, fp)
        let! _ = job.Run(QualityHistoryMode.Rebuild (day.AddDays 2.0), CancellationToken.None)
        let! n = conn.QuerySingleAsync<int64>("SELECT count(*) FROM moderation_quality_daily WHERE day = @day AND tp + tn + fp + fn = 0", {| day = day.AddDays 2.0 |})
        Assert.Equal(2L, n)
    }

    [<Fact>]
    member _.``Future dates are rejected`` () = task {
        let! _ = Assert.ThrowsAsync<ArgumentException>(fun () -> job.Run(QualityHistoryMode.Rebuild (DateTime.UtcNow.Date.AddDays 1.0), CancellationToken.None))
        ()
    }

    [<Fact>]
    member _.``Backfill resumes missing UTC days and leaves the rolling window to daily refresh`` () = task {
        let start = DateTime(1999, 2, 1, 0, 0, 0, DateTimeKind.Utc)
        let clock = { new TimeProvider() with override _.GetUtcNow() = DateTimeOffset(start.AddDays 18.0) }
        let history = ModerationQualityHistory(fixture.DbConnectionString, clock)
        do! fixture.InsertRawEvent<DetectionEvent>($"detection:{chat}:93001", 1,
            MlScoredMessage {| chatId = chat; messageId = 93001L; score = -2.0; isSpam = false |}, start.AddHours 1.0)
        let! count = history.Run(QualityHistoryMode.Backfill, CancellationToken.None)
        Assert.Equal(4, count)
        let! resumed = history.Run(QualityHistoryMode.Backfill, CancellationToken.None)
        Assert.Equal(0, resumed)
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        let! recentBefore = conn.QuerySingleAsync<int64>("SELECT count(*) FROM moderation_quality_daily WHERE day >= @first AND day <= @last", {| first = start.AddDays 4.0; last = start.AddDays 18.0 |})
        Assert.Equal(0L, recentBefore)
        let! _ = history.Run(QualityHistoryMode.Daily, CancellationToken.None)
        let! recentAfter = conn.QuerySingleAsync<int64>("SELECT count(DISTINCT day) FROM moderation_quality_daily WHERE day >= @first AND day <= @last", {| first = start.AddDays 4.0; last = start.AddDays 18.0 |})
        Assert.Equal(15L, recentAfter)
    }

    [<Fact>]
    member _.``History endpoint validates dates and requires authentication`` () = task {
        use payload = new System.Net.Http.StringContent("")
        use! invalid = fixture.BotHttp.PostAsync("/quality-history?day=invalid", payload)
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode)
        use! future = fixture.BotHttp.PostAsync("/quality-history?day=9999-01-01", payload)
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, future.StatusCode)
        use client = new System.Net.Http.HttpClient(BaseAddress = fixture.BotHttp.BaseAddress)
        use! unauthorized = client.PostAsync("/quality-history", payload)
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauthorized.StatusCode)
    }

    [<Fact>]
    member _.``Daily refresh replaces today's provisional counts`` () = task {
        let today = DateTime(2010, 1, 15, 0, 0, 0, DateTimeKind.Utc)
        let history = historyAt today
        let id = 94001L
        do! scoredAt today id 0.7
        let! _ = history.Run(QualityHistoryMode.Daily, CancellationToken.None)
        let! before = mlAt today
        Assert.Equal((0L, 1L), (before.tp, before.fp))
        do! fixture.InsertRawEvent<MessageEvent>($"message:{chat}:{id}", 2,
            MessageMarkedSpam {| chatId = chat; messageId = id; markedBy = Some 1L |}, today.AddHours 1.0)
        let! _ = history.Run(QualityHistoryMode.Daily, CancellationToken.None)
        let! after = mlAt today
        Assert.Equal((1L, 0L), (after.tp, after.fp))
    }

    [<Fact>]
    member _.``An old edited message rebuilds its original day without double counting today`` () = task {
        let today = DateTime(2011, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let original = today.AddDays -149.0
        let history = historyAt today
        let id = 95001L
        do! scoredAt original id -2.0
        let! _ = history.Run(QualityHistoryMode.Daily, CancellationToken.None)
        let! before = mlAt original
        Assert.Equal(1L, before.tn)
        do! fixture.InsertRawEvent<MessageEvent>($"message:{chat}:{id}", 2,
            MessageEdited {| chatId = chat; messageId = id; userId = id; text = Some "edited"; rawMessage = "{}" |}, today)
        do! fixture.InsertRawEvent<DetectionEvent>($"detection:{chat}:{id}", 2,
            MlScoredMessage {| chatId = chat; messageId = id; score = 0.7; isSpam = true |}, today.AddMinutes 1.0)
        let! _ = history.Run(QualityHistoryMode.Daily, CancellationToken.None)
        let! after = mlAt original
        Assert.Equal((0L, 1L), (after.tn, after.fp))
        let! recent = mlAt today
        Assert.Equal(0L, recent.tp + recent.tn + recent.fp + recent.fn)
    }

    [<Fact>]
    member _.``A lower event id committed after a refresh is processed and rollback leaves no pending work`` () = task {
        let today = DateTime(2012, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let original = today.AddDays -50.0
        let history = historyAt today
        let id = 96001L
        do! scoredAt original id 2.0
        do! fixture.InsertRawEvent<ModerationEvent>($"moderation:{chat}:{id}", 1,
            BotAutoDeleted {| chatId = chat; messageId = id; userId = id; reason = MlSpam {| score = 2.0 |} |}, original.AddMinutes 2.0)
        let! _ = history.Run(QualityHistoryMode.Daily, CancellationToken.None)
        use writer = new NpgsqlConnection(fixture.DbConnectionString)
        do! writer.OpenAsync()
        use! tx = writer.BeginTransactionAsync()
        let ham = MessageMarkedHam {| chatId = chat; messageId = id; text = "example"; markedBy = Some 1L |}
        let! lowerId = writer.QuerySingleAsync<int64>("INSERT INTO event(stream_id,stream_version,data,created_at) VALUES (@stream,2,@data::jsonb,@at) RETURNING id",
            {| stream = $"message:{chat}:{id}"; data = JsonSerializer.Serialize(ham, eventJsonOpts); at = today |}, tx)
        do! scoredAt today 96002L -2.0
        use reader = new NpgsqlConnection(fixture.DbConnectionString)
        let! higherId = reader.QuerySingleAsync<int64>("SELECT max(id) FROM event")
        Assert.True(higherId > lowerId)
        let! _ = history.Run(QualityHistoryMode.Daily, CancellationToken.None)
        let! before = mlAt original
        Assert.Equal(1L, before.tp)
        do! tx.CommitAsync()
        let! _ = history.Run(QualityHistoryMode.Daily, CancellationToken.None)
        let! after = mlAt original
        Assert.Equal((0L, 1L), (after.tp, after.fp))
        use! rolledBack = writer.BeginTransactionAsync()
        let spam = MessageMarkedSpam {| chatId = chat; messageId = id; markedBy = Some 1L |}
        let! rolledId = writer.QuerySingleAsync<int64>("INSERT INTO event(stream_id,stream_version,data,created_at) VALUES (@stream,3,@data::jsonb,@at) RETURNING id",
            {| stream = $"message:{chat}:{id}"; data = JsonSerializer.Serialize(spam, eventJsonOpts); at = today.AddHours 1.0 |}, rolledBack)
        do! rolledBack.RollbackAsync()
        let! pending = reader.QuerySingleAsync<int64>("SELECT count(*) FROM moderation_quality_pending WHERE event_id = @id", {| id = rolledId |})
        Assert.Equal(0L, pending)
    }

    [<Fact>]
    member _.``A failed old-day rebuild preserves its dirty marker for retry`` () = task {
        let today = DateTime(2013, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        let original = today.AddDays -40.0
        let history = historyAt today
        let id = 97001L
        do! scoredAt original id -2.0
        let! _ = history.Run(QualityHistoryMode.Daily, CancellationToken.None)
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        let malformed = JsonSerializer.Serialize {| Case = "MessageEdited"; chatId = chat; messageId = id; userId = id; text = [|"invalid"|]; rawMessage = "{}" |}
        let! brokenId = conn.QuerySingleAsync<int64>("INSERT INTO event(stream_id,stream_version,data,created_at) VALUES (@stream,2,@data::jsonb,@at) RETURNING id",
            {| stream = $"message:{chat}:{id}"; data = malformed; at = today |})
        let! _ = Assert.ThrowsAnyAsync<Exception>(fun () -> history.Run(QualityHistoryMode.Daily, CancellationToken.None))
        let! dirty = conn.QuerySingleAsync<int64>("SELECT count(*) FROM moderation_quality_dirty_day WHERE day = @day", {| day = original |})
        Assert.Equal(1L, dirty)
        let! unchanged = mlAt original
        Assert.Equal(1L, unchanged.tn)
        let! _ = conn.ExecuteAsync("DELETE FROM event WHERE id = @id", {| id = brokenId |})
        let! _ = history.Run(QualityHistoryMode.Daily, CancellationToken.None)
        let! remaining = conn.QuerySingleAsync<int64>("SELECT count(*) FROM moderation_quality_dirty_day WHERE day = @day", {| day = original |})
        Assert.Equal(0L, remaining)
    }
