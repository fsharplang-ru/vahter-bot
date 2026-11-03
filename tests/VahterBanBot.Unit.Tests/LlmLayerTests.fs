module VahterBanBot.Unit.Tests.LlmLayerTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open BotInfra
open BotInfra.JsonSetup
open VahterBanBot.Utils
open VahterBanBot.Types
open VahterBanBot.LlmTriagePipeline
open VahterBanBot.LlmLayerConfiguration
open VahterBanBot.LlmVerdictCache
open Xunit

let private context = { ChatDescription = "\nChat: technical discussion"; UserPrompt = "frozen evidence" }
let private logger = NullLogger.Instance
let private result verdict = { Verdict = verdict; PromptTokens = 1; CompletionTokens = 1; LatencyMs = 1 }
let private layer id classify =
    { Id = LlmLayerId.AzureSol56
      Source = { ModelName = id + "-model"; PromptHash = id + "-prompt" }
      CacheIdentity = id; Classify = classify }
let private settings () =
    { fromJson<BotConfiguration> "{}" with
        LlmTriageLayers = LlmLayerOrder.Default
        LlmChatDescriptions = Dictionary<int64, string>() }

[<Theory>]
[<InlineData("SPAM")>]
[<InlineData("NOT_SPAM")>]
let ``a firm first verdict stops subsequent layers`` verdict = task {
    let first = layer "first" (fun _ -> Task.FromResult(result (LlmVerdict.FromString(verdict, Some "first reason", None))))
    let second = layer "second" (fun _ -> failwith "second layer must not run")
    let! actual = runLayers logger true [first; second] context CancellationToken.None
    Assert.Equal("first-model", actual.Source.Value.ModelName)
    Assert.Equal(LlmVerdict.FromString(verdict, Some "first reason", None), actual.Classification.Verdict)
}

[<Theory>]
[<InlineData("SKIP")>]
[<InlineData("ERROR")>]
[<InlineData("exception")>]
[<InlineData("timeout")>]
let ``abstentions and failures escalate with identical evidence`` outcome = task {
    let seen = ResizeArray<LlmTriageContext>()
    let first = layer "first" (fun (input, _) ->
        seen.Add input
        if outcome = "exception" then raise (InvalidOperationException "unavailable")
        if outcome = "timeout" then raise (OperationCanceledException "provider timeout")
        Task.FromResult(result (LlmVerdict.FromString(outcome, Some "private first-layer reason", None))))
    let second = layer "second" (fun (input, _) ->
        seen.Add input
        Task.FromResult(result (LlmVerdict.Kill (Some "second reason", None))))
    let! actual = runLayers logger true [first; second] context CancellationToken.None
    Assert.Equal("second-model", actual.Source.Value.ModelName)
    Assert.Equal(LlmVerdict.Kill (Some "second reason", None), actual.Classification.Verdict)
    Assert.Equal(2, seen.Count)
    Assert.Same(context, seen[0])
    Assert.Same(context, seen[1])
}

[<Theory>]
[<InlineData("SKIP")>]
[<InlineData("ERROR")>]
let ``exhausted layers preserve the last human-review outcome`` verdict = task {
    let first = layer "first" (fun _ -> Task.FromResult(result LlmVerdict.Error))
    let expected = LlmVerdict.FromString(verdict, Some "last reason", None)
    let last = layer "last" (fun _ -> Task.FromResult(result expected))
    let! actual = runLayers logger true [first; last] context CancellationToken.None
    Assert.Equal("last-model", actual.Source.Value.ModelName)
    Assert.Equal(expected, actual.Classification.Verdict)
}

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``content filter policy controls whether another layer is tried`` contentFilterIsSpam = task {
    let first = layer "first" (fun _ -> Task.FromResult(result (LlmVerdict.ContentFiltered "sexual=high")))
    let second = layer "second" (fun _ -> Task.FromResult(result (LlmVerdict.NotSpam (None, None))))
    let! actual = runLayers logger contentFilterIsSpam [first; second] context CancellationToken.None
    Assert.Equal((if contentFilterIsSpam then "first-model" else "second-model"), actual.Source.Value.ModelName)
}

[<Fact>]
let ``cancellation stops escalation and cannot yield an automatic verdict`` () = task {
    use cts = new CancellationTokenSource()
    let first = layer "first" (fun (_, ct) ->
        Assert.Equal(cts.Token, ct)
        cts.Cancel()
        Task.FromResult(result (LlmVerdict.Kill (None, None))))
    let second = layer "second" (fun _ -> failwith "cancelled pipeline must not escalate")
    let! actual = runLayers logger true [first; second] context cts.Token
    Assert.Equal(LlmVerdict.Error, actual.Classification.Verdict)
}

[<Fact>]
let ``pipeline order selects the first accepting provider`` () = task {
    let visited = ResizeArray<string>()
    let provider name = layer name (fun _ ->
        visited.Add name
        Task.FromResult(result (LlmVerdict.NotSpam (None, None))))
    let first, second = provider "first", provider "second"
    for order in [[first; second]; [second; first]] do
        let! actual = runLayers logger true order context CancellationToken.None
        Assert.Equal(Some order.Head.Source, actual.Source)
    Assert.Equal<string list>(["first"; "second"], List.ofSeq visited)
}

[<Theory>]
[<InlineData("[]")>]
[<InlineData("[\"azure-sol-5.6\",\"azure-sol-5.6\"]")>]
[<InlineData("[\"\"]")>]
[<InlineData("[\"unknown-layer\"]")>]
[<InlineData("[null]")>]
let ``invalid orders fail during settings parsing`` json =
    %Assert.Throws<ArgumentException>(fun () -> %(json |> fromJson<string list> |> LlmLayerOrder.Parse))

[<Fact>]
let ``settings parsing returns the supported versioned layer`` () =
    let id = LlmLayerId.AzureSol56
    let order = LlmLayerOrder.Parse [id.SettingValue]
    Assert.Equal<LlmLayerId list>([id], order.Layers)
    Assert.Equal(LlmLayerOrder.Default, order)

[<Fact>]
let ``empty pipeline returns an error without inventing a provider`` () = task {
    let! actual = runLayers logger true [] context CancellationToken.None
    Assert.Equal(LlmVerdict.Error, actual.Classification.Verdict)
    Assert.True(actual.Source.IsNone)
}

[<Fact>]
let ``cancelled pipeline never invokes a provider`` () = task {
    use cts = new CancellationTokenSource()
    cts.Cancel()
    let provider = layer "only" (fun _ -> failwith "cancelled pipeline must not call a provider")
    let! actual = runLayers logger true [provider] context cts.Token
    Assert.Equal(LlmVerdict.Error, actual.Classification.Verdict)
}

[<Fact>]
let ``unknown persisted cache verdicts do not enter the domain`` () =
    Assert.True((LlmCacheVerdict.TryParse "foo").IsNone)

[<Fact>]
let ``cache identities isolate order model prompt gate and policy changes`` () =
    let conf = settings ()
    let first = layer "first" (fun _ -> Task.FromResult(result LlmVerdict.Error))
    let second = layer "second" first.Classify
    let original = pipelineCacheIdentity conf [first; second]
    Assert.NotEqual<string>(original, pipelineCacheIdentity conf [second; first])
    Assert.NotEqual<string>(original, pipelineCacheIdentity conf [{first with CacheIdentity = "updated-model-prompt-gate"}; second])
    let descriptions = Dictionary<int64, string>()
    descriptions.Add(42L, "different policy")
    Assert.NotEqual<string>(original, pipelineCacheIdentity {conf with LlmChatDescriptions = descriptions} [first; second])

[<Fact>]
let ``disabled content-filter action remains human review after exhaustion`` () = task {
    let provider = layer "only" (fun _ -> Task.FromResult(result (LlmVerdict.ContentFiltered "sexual=high")))
    let! actual = runLayers logger false [provider] context CancellationToken.None
    Assert.Equal(LlmVerdict.Error, actual.Classification.Verdict)
}
