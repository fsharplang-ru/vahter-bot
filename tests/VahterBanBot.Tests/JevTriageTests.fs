module VahterBanBot.Tests.JevTriageTests

open System
open System.Net.Http.Json
open System.Text.Json
open System.Threading.Tasks
open BotInfra
open BotTestInfra
open VahterBanBot.Tests.ContainerTestBase
open Xunit

type JevTriageTests(fixture: JevVahterTestContainers) =
    let cascade = "[\"typesafe-jev-1.14.0\",\"azure-sol-5.6\"]"
    let answer label =
        let spam, ham, skip =
            match label with
            | "SPAM" -> 0.9, 0.05, 0.05
            | "NOT_SPAM" -> 0.05, 0.9, 0.05
            | _ -> 0.05, 0.05, 0.9
        JsonSerializer.Serialize {| model = "jev-1.14.0"; usage = {| input_tokens = 123; output_tokens = 45 |}; answers = {| verdict = {| ``type`` = "choice"; choice = label; confidence = 0.9; probabilities = {| SPAM = spam; NOT_SPAM = ham; SKIP = skip |} |}; review_focus = {| choice = "unclear" |} |} |}
    let script status body delay = task {
        let payload = {| responses = [| {| status = status; body = body; delayMs = delay; errorMode = "" |} |] |}
        use! response = fixture.FakeAzureHttp.PostAsJsonAsync("/test/mock/jev-script", payload)
        %response.EnsureSuccessStatusCode()
    }
    let reset () = task {
        do! fixture.ClearAzureOcrCalls()
        do! fixture.ClearFakeCalls()
        do! fixture.ClearLlmVerdictCache()
        do! fixture.SetAzureLlmScript [||]
    }
    let send name = task {
        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = Tg.user(firstName = name))
        let! _ = fixture.SendMessage msg
        return msg.Message.Value
    }
    let jevCalls () = task {
        let! calls = fixture.FakeAzureHttp.GetFromJsonAsync<FakeCall array>("/test/calls")
        return calls |> Array.filter (fun call -> call.Url = "/v1/systemone")
    }

    [<Theory>]
    [<InlineData("SPAM", true)>]
    [<InlineData("NOT_SPAM", false)>]
    let ``Jev automatic decisions stop Sol and record the deciding model`` verdict shouldDelete = task {
        do! reset ()
        do! script 200 (answer verdict) 0
        let! msg = send "kill Jev automatic"
        let! deleted = fixture.MessageIsAutoDeleted msg
        Assert.Equal(shouldDelete, deleted)
        let! actual = fixture.TryGetLlmTriageVerdict msg
        Assert.Equal(Some verdict, actual)
        let! model = fixture.TryGetLlmClassifiedModelName msg
        Assert.Equal(Some "jev-1.14.0", model)
        let! reason = fixture.TryGetLlmClassifiedReason msg
        Assert.Equal(Some $"{verdict} probability: 0.900", reason)
        let! sol = fixture.GetAzureLlmCalls()
        Assert.Empty(sol)
        let! jev = jevCalls ()
        %Assert.Single(jev)
        if shouldDelete then
            let! next = send "another sender"
            let! cachedDeleted = fixture.MessageIsAutoDeleted next
            Assert.True(cachedDeleted)
            let! calls = jevCalls ()
            %Assert.Single(calls)
            let! cached = fixture.TryGetLlmVerdictCacheHit next
            Assert.Equal(Some ("SPAM", Some "SPAM probability: 0.900", "global"), cached)
            let! messages = fixture.GetFakeCalls "sendMessage"
            Assert.Contains(messages, fun message -> message.Body.Contains("jev-1.14.0") && message.Body.Contains("cached/global") && message.Body.Contains("SPAM probability: 0.900"))
    }

    [<Theory>]
    [<InlineData("skip", 200, 0)>]
    [<InlineData("uncertain", 200, 0)>]
    [<InlineData("malformed", 200, 0)>]
    [<InlineData("invalid-choice", 200, 0)>]
    [<InlineData("unauthorized", 401, 0)>]
    [<InlineData("unavailable", 503, 0)>]
    [<InlineData("retired-model", 422, 0)>]
    [<InlineData("model-mismatch", 200, 0)>]
    [<InlineData("timeout", 200, 4000)>]
    let ``Jev failures and uncertainty fall through to Sol with unchanged evidence`` outcome status delay = task {
        do! reset ()
        let body =
            match outcome with
            | "skip" -> answer "SKIP"
            | "invalid-choice" -> (answer "SPAM").Replace("\"choice\":\"SPAM\"", "\"choice\":\"UNKNOWN\"")
            | "uncertain" -> (answer "SPAM").Replace("\"confidence\":0.9", "\"confidence\":0.1")
            | "timeout" -> answer "NOT_SPAM"
            | "model-mismatch" -> (answer "SPAM").Replace("jev-1.14.0", "synthetic-model")
            | _ -> "{}"
        do! script status body delay
        let! msg = send "kill Jev fallback"
        let! deleted = fixture.MessageIsAutoDeleted msg
        Assert.True(deleted)
        let! model = fixture.TryGetLlmClassifiedModelName msg
        Assert.NotEqual(Some "jev-1.14.0", model)
        let! sol = fixture.GetAzureLlmCalls()
        let! jev = jevCalls ()
        %Assert.Single(sol)
        %Assert.Single(jev)
        use jevRequest = JsonDocument.Parse jev[0].Body
        use state = JsonDocument.Parse(jevRequest.RootElement.GetProperty("state").GetString())
        use solRequest = JsonDocument.Parse sol[0].Body
        let solUser =
            solRequest.RootElement.GetProperty("messages").EnumerateArray()
            |> Seq.find (fun item -> item.GetProperty("role").GetString() = "user")
            |> fun item -> item.GetProperty("content").GetString()
        Assert.Equal(state.RootElement.GetProperty("record").GetString(), solUser)
        Assert.DoesNotContain("review_focus", sol[0].Body)
    }

    [<Fact>]
    let ``changing order or disabling Jev takes effect on reload`` () = task {
        for order in ["[\"azure-sol-5.6\",\"typesafe-jev-1.14.0\"]"; "[\"azure-sol-5.6\"]"] do
            do! reset ()
            do! fixture.SetBotSetting("LLM_TRIAGE_LAYERS", order)
            do! fixture.ReloadSettings()
            let! msg = send "kill Sol first"
            let! deleted = fixture.MessageIsAutoDeleted msg
            Assert.True(deleted)
            let! jev = jevCalls ()
            Assert.Empty(jev)
    }

    [<Theory>]
    [<InlineData("JEV_MIN_CONFIDENCE", "0.95")>]
    [<InlineData("JEV_MIN_PROBABILITY", "0.95")>]
    [<InlineData("JEV_MIN_MARGIN", "0.90")>]
    let ``stricter Jev gate invalidates cached spam and allows Sol to reconsider`` key value = task {
        do! reset ()
        do! script 200 (answer "SPAM") 0
        let! first = send "ordinary sender"
        let! deleted = fixture.MessageIsAutoDeleted first
        Assert.True(deleted)
        do! fixture.SetBotSetting(key, value)
        do! fixture.ReloadSettings()
        do! script 200 (answer "SPAM") 0
        let! second = send "another ordinary sender"
        let! deleted = fixture.MessageIsAutoDeleted second
        Assert.False(deleted)
        let! sol = fixture.GetAzureLlmCalls()
        %Assert.Single(sol)
        let! jev = jevCalls ()
        Assert.Equal(2, jev.Length)
    }

    interface IAsyncDisposable with
        member _.DisposeAsync() = ValueTask(task {
            do! fixture.SetBotSetting("LLM_TRIAGE_LAYERS", cascade)
            do! fixture.SetBotSetting("JEV_MIN_CONFIDENCE", "0.50")
            do! fixture.SetBotSetting("JEV_MIN_PROBABILITY", "0.75")
            do! fixture.SetBotSetting("JEV_MIN_MARGIN", "0.30")
            do! fixture.ReloadSettings()
        } :> Task)
