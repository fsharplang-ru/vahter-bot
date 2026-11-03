module VahterBanBot.Tests.LlmLayerConfigurationTests

open System
open System.Threading.Tasks
open System.Text.Json
open Npgsql
open Dapper
open VahterBanBot.Tests.ContainerTestBase
open BotInfra
open BotTestInfra
open Xunit

type LlmLayerConfigurationTests(fixture: MlEnabledVahterTestContainers, _ml: MlAwaitFixture) =
    let mutable restore = []
    let stageSetting key value = task {
        let! previous = fixture.GetBotSetting key
        restore <- (key, previous) :: restore
        do! fixture.SetBotSetting(key, value)
    }
    let change key value = task {
        do! stageSetting key value
        do! fixture.ReloadSettings()
    }
    let reset () = task {
        do! fixture.ClearAzureOcrCalls()
        do! fixture.ClearFakeCalls()
        do! fixture.ClearLlmVerdictCache()
    }

    [<Theory>]
    [<InlineData("[\"unknown-layer\",\"azure-sol-5.6\"]")>]
    [<InlineData("[\"azure-sol-5.6\",\"azure-sol-5.6\"]")>]
    [<InlineData("[]")>]
    [<InlineData("null")>]
    [<InlineData("{\"layers\":[]}")>]
    [<InlineData("[\"typesafe-jev-1.14.0\",\"typesafe-jev-1.14.0\"]")>]
    let ``invalid layer settings reject reload and preserve the active pipeline`` setting = task {
        do! reset ()
        let spam () = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = Tg.user(firstName = "kill layer-config"))
        let first = spam ()
        let! _ = fixture.SendMessage first
        let! deleted = fixture.MessageIsAutoDeleted first.Message.Value
        Assert.True(deleted)
        do! stageSetting "LLM_TRIAGE_LAYERS" setting
        use! rejected = fixture.Http.PostAsync("/reload-settings", null)
        Assert.False(rejected.IsSuccessStatusCode)
        do! fixture.ClearLlmVerdictCache()
        let next = spam ()
        let! _ = fixture.SendMessage next
        let! deletedNext = fixture.MessageIsAutoDeleted next.Message.Value
        Assert.True(deletedNext)
        let! calls = fixture.GetAzureLlmCalls()
        Assert.Equal(2, calls.Length)
        use! dump = fixture.Http.GetAsync("/config-dump")
        %dump.EnsureSuccessStatusCode()
        let! body = dump.Content.ReadAsStringAsync()
        use parsed = JsonDocument.Parse body
        Assert.True(parsed.RootElement.TryGetProperty("LlmTriageLayers") |> fst)
    }

    [<Theory>]
    [<InlineData("AZURE_OPENAI_DEPLOYMENT", "synthetic-model")>]
    [<InlineData("LLM_REASONING_EFFORT", "none")>]
    [<InlineData("CHAT_DESCRIPTIONS_JSON", "{\"-666\":\"Only technical discussion\"}")>]
    let ``settings reload cannot reuse a verdict from a different pipeline policy`` key value = task {
        do! reset ()
        let first = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = Tg.user(firstName = "kill before-reload"))
        let! _ = fixture.SendMessage first
        let! deleted = fixture.MessageIsAutoDeleted first.Message.Value
        Assert.True(deleted)
        do! change key value
        let next = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = Tg.user())
        let! _ = fixture.SendMessage next
        let! deletedNext = fixture.MessageIsAutoDeleted next.Message.Value
        Assert.False(deletedNext)
        let! verdict = fixture.TryGetLlmTriageVerdict next.Message.Value
        Assert.Equal(Some "NOT_SPAM", verdict)
        let! calls = fixture.GetAzureLlmCalls()
        Assert.Equal(2, calls.Length)
    }

    [<Fact>]
    let ``enabled Jev without credentials keeps serving through Sol`` () = task {
        do! reset ()
        do! change "LLM_TRIAGE_LAYERS" "[\"typesafe-jev-1.14.0\",\"azure-sol-5.6\"]"
        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = Tg.user(firstName = "kill missing Jev key"))
        let! _ = fixture.SendMessage msg
        let! deleted = fixture.MessageIsAutoDeleted msg.Message.Value
        Assert.True(deleted)
        let! calls = fixture.GetAzureLlmCalls()
        %Assert.Single(calls)
        use! health = fixture.Http.GetAsync("/health")
        Assert.True(health.IsSuccessStatusCode)
    }

    interface IAsyncDisposable with
        member _.DisposeAsync() = ValueTask(task {
            for key, value in restore do
                match value with
                | Some previous -> do! fixture.SetBotSetting(key, previous)
                | None ->
                    use conn = new NpgsqlConnection(fixture.DbConnectionString)
                    let! _ = conn.ExecuteAsync("DELETE FROM bot_setting WHERE key = @key", {| key = key |})
                    ()
            do! fixture.ReloadSettings()
        } :> Task)

    interface IClassFixture<MlAwaitFixture>
