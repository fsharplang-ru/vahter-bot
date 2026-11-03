module VahterBanBot.Unit.Tests.JevLayerTests

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Threading
open System.Threading.Tasks
open System.Text.Json.Nodes
open Microsoft.Extensions.Logging.Abstractions
open BotInfra
open BotInfra.JsonSetup
open VahterBanBot.Types
open VahterBanBot.LlmLayerConfiguration
open VahterBanBot.LlmTriagePipeline
open VahterBanBot.JevTriage
open Xunit

let private configuration timeout confidence probability margin =
    JevConfiguration.Parse("https://api.typesafe.ai/v1/systemone", timeout, confidence, probability, margin)
let private settings () =
    { fromJson<BotConfiguration> "{}" with
        TypesafeApiKey = "synthetic-key"
        Jev = configuration "3000" "0.50" "0.75" "0.30" }
let private context = { ChatDescription = "Technical chat"; UserPrompt = "Frozen untrusted message and profile" }
let private answer = """{"model":"jev-1.14.0","answers":{"verdict":{"type":"choice","choice":"SPAM","confidence":0.5,"probabilities":{"SPAM":0.75,"NOT_SPAM":0.2,"SKIP":0.05}}},"usage":{"input_tokens":123,"output_tokens":45}}"""

type private Handler(send: HttpRequestMessage -> CancellationToken -> Task<HttpResponseMessage>) =
    inherit HttpMessageHandler()
    override _.SendAsync(request, ct) = send request ct

let private provider send =
    let handler = new Handler(send)
    let factory = { new IHttpClientFactory with member _.CreateClient(_) = new HttpClient(handler, false) }
    let layer = TypesafeJev114Layer(factory, NullLogger<TypesafeJev114Layer>.Instance, TimeProvider.System) :> ILlmTriageLayer
    handler, layer
let private response body = new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent(body))

[<Theory>]
[<InlineData("SPAM")>]
[<InlineData("NOT_SPAM")>]
let ``accepted Jev choice returns the provider verdict and usage`` label = task {
    let body = if label = "SPAM" then answer else answer.Replace("SPAM", "TEMP").Replace("NOT_TEMP", "SPAM").Replace("TEMP", "NOT_SPAM")
    let handler, layer = provider (fun request _ -> task {
        Assert.Equal("Bearer", request.Headers.Authorization.Scheme)
        Assert.Equal("synthetic-key", request.Headers.Authorization.Parameter)
        let! json = request.Content.ReadAsStringAsync()
        let request = JsonNode.Parse json
        Assert.Equal("jev-1.14.0", request.["model"].GetValue<string>())
        let state = JsonNode.Parse(request.["state"].GetValue<string>())
        Assert.Equal(context.UserPrompt, state.["record"].GetValue<string>())
        Assert.Equal(context.ChatDescription, state.["chat_policy"].GetValue<string>())
        Assert.Equal(2, request.["questions"].AsObject().Count)
        let criteria = request.["questions"].["verdict"].["criteria"].AsObject()
        Assert.Equal<string array>([| "NOT_SPAM"; "SKIP"; "SPAM" |], criteria |> Seq.map _.Key |> Seq.sort |> Seq.toArray)
        Assert.Contains("FIRST-PASS ROLE", request.["questions"].["verdict"].["instructions"].GetValue<string>())
        Assert.Equal("choice", request.["questions"].["review_focus"].["type"].GetValue<string>())
        return response body
    })
    use _handler = handler
    let configured = layer.Configure(settings ())
    let! result = configured.Classify(context, CancellationToken.None)
    Assert.Equal(LlmVerdict.FromString(label, Some $"{label} probability: 0.750", None), result.Verdict)
    Assert.Equal(123, result.PromptTokens)
    Assert.Equal(45, result.CompletionTokens)
    Assert.Equal("jev-1.14.0", configured.Source.ModelName)
}

[<Theory>]
[<InlineData("choice", "\"SKIP\"")>]
[<InlineData("choice", "\"OTHER\"")>]
[<InlineData("choice", "null")>]
[<InlineData("type", "\"noul\"")>]
[<InlineData("confidence", "0.499")>]
[<InlineData("confidence", "true")>]
[<InlineData("confidence", "\"0.9\"")>]
[<InlineData("confidence", "-0.1")>]
[<InlineData("confidence", "1.1")>]
[<InlineData("confidence", "1e999")>]
[<InlineData("probabilities", "{\"SPAM\":0.74,\"NOT_SPAM\":0.20,\"SKIP\":0.06}")>]
[<InlineData("probabilities", "{\"SPAM\":0.9,\"NOT_SPAM\":0.2,\"SKIP\":0.1}")>]
[<InlineData("probabilities", "{\"SPAM\":0.8,\"NOT_SPAM\":0.2}")>]
[<InlineData("probabilities", "{\"SPAM\":true,\"NOT_SPAM\":0,\"SKIP\":0}")>]
[<InlineData("probabilities", "{\"SPAM\":1.1,\"NOT_SPAM\":-0.1,\"SKIP\":0}")>]
[<InlineData("probabilities", "{\"SPAM\":0.1,\"NOT_SPAM\":0.9,\"SKIP\":0}")>]
[<InlineData("probabilities", "null")>]
[<InlineData("probabilities", "{\"SPAM\":0.8,\"NOT_SPAM\":0.1,\"SKIP\":0.1,\"OTHER\":0}")>]
let ``invalid or uncertain choices never produce automatic decisions`` (field: string) (value: string) = task {
    let body = JsonNode.Parse answer
    body.["answers"].["verdict"].[field] <- JsonNode.Parse value
    let handler, layer = provider (fun _ _ -> Task.FromResult(response (body.ToJsonString())))
    use _handler = handler
    let! result = (layer.Configure(settings ())).Classify(context, CancellationToken.None)
    match result.Verdict with
    | LlmVerdict.Skip _ | LlmVerdict.Error -> ()
    | verdict -> Assert.Fail($"Unsafe verdict {verdict}")
}

[<Theory>]
[<InlineData("{}")>]
[<InlineData("null")>]
[<InlineData("[]")>]
[<InlineData("not json")>]
[<InlineData("{\"answers\":{\"verdict\":{\"refusal\":true}}}")>]
let ``malformed and refused responses fail closed`` body = task {
    let handler, layer = provider (fun _ _ -> Task.FromResult(response body))
    use _handler = handler
    let! result = (layer.Configure(settings ())).Classify(context, CancellationToken.None)
    Assert.Equal(LlmVerdict.Error, result.Verdict)
}

[<Theory>]
[<InlineData("type")>]
[<InlineData("choice")>]
[<InlineData("confidence")>]
[<InlineData("probabilities")>]
let ``missing required answer fields never become default values`` field = task {
    let body = JsonNode.Parse answer
    %body.["answers"].["verdict"].AsObject().Remove(field)
    let handler, layer = provider (fun _ _ -> Task.FromResult(response (body.ToJsonString())))
    use _handler = handler
    let conf = { settings () with Jev = configuration "3000" "0" "0" "0" }
    let! result = (layer.Configure conf).Classify(context, CancellationToken.None)
    Assert.Equal(LlmVerdict.Error, result.Verdict)
}

[<Theory>]
[<InlineData("null", 0, 0)>]
[<InlineData("{}", 0, 0)>]
[<InlineData("{\"input_tokens\":17}", 17, 0)>]
[<InlineData("{\"output_tokens\":23}", 0, 23)>]
let ``optional usage is decoded without affecting the verdict`` usage inputTokens outputTokens = task {
    let body = JsonNode.Parse answer
    body.["usage"] <- JsonNode.Parse(usage: string)
    let handler, layer = provider (fun _ _ -> Task.FromResult(response (body.ToJsonString())))
    use _handler = handler
    let! result = (layer.Configure(settings ())).Classify(context, CancellationToken.None)
    Assert.Equal(LlmVerdict.Kill(Some "SPAM probability: 0.750", None), result.Verdict)
    Assert.Equal(inputTokens, result.PromptTokens)
    Assert.Equal(outputTokens, result.CompletionTokens)
}

[<Fact>]
let ``duplicate probability keys cannot overwrite an invalid distribution`` () = task {
    let body = answer.Replace("\"SPAM\":0.75", "\"SPAM\":0.01,\"SPAM\":0.75")
    let handler, layer = provider (fun _ _ -> Task.FromResult(response body))
    use _handler = handler
    let! result = (layer.Configure(settings ())).Classify(context, CancellationToken.None)
    Assert.Equal(LlmVerdict.Error, result.Verdict)
}

[<Theory>]
[<InlineData(401)>]
[<InlineData(403)>]
[<InlineData(500)>]
let ``service errors make one request and return a layer failure`` status = task {
    let mutable calls = 0
    let handler, layer = provider (fun _ _ ->
        calls <- calls + 1
        Task.FromResult(new HttpResponseMessage(enum<HttpStatusCode> status)))
    use _handler = handler
    let! result = (layer.Configure(settings ())).Classify(context, CancellationToken.None)
    Assert.Equal(LlmVerdict.Error, result.Verdict)
    Assert.Equal(1, calls)
}

[<Theory>]
[<InlineData("")>]
[<InlineData("   ")>]
let ``missing key skips Jev without an HTTP call`` key = task {
    let handler, layer = provider (fun _ _ -> failwith "Missing key must not call the provider")
    use _handler = handler
    let! result = (layer.Configure({settings () with TypesafeApiKey = key})).Classify(context, CancellationToken.None)
    Assert.Equal(LlmVerdict.Error, result.Verdict)
}

[<Fact>]
let ``timeout fails only Jev and preserves the caller cancellation budget`` () = task {
    let handler, layer = provider (fun _ ct -> task {
        do! Task.Delay(Timeout.Infinite, ct)
        return response answer
    })
    use _handler = handler
    use caller = new CancellationTokenSource()
    let conf = {settings () with Jev = configuration "10" "0.50" "0.75" "0.30"}
    let! result = (layer.Configure conf).Classify(context, caller.Token)
    Assert.Equal(LlmVerdict.Error, result.Verdict)
    Assert.False(caller.IsCancellationRequested)
}

[<Fact>]
let ``Retry-After prevents subsequent Jev requests during cooldown`` () = task {
    let mutable calls = 0
    let handler, layer = provider (fun _ _ ->
        calls <- calls + 1
        let response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        response.Headers.RetryAfter <- RetryConditionHeaderValue(TimeSpan.FromHours 1.0)
        Task.FromResult response)
    use _handler = handler
    for _ in 1..2 do
        let! result = (layer.Configure(settings ())).Classify(context, CancellationToken.None)
        Assert.Equal(LlmVerdict.Error, result.Verdict)
    Assert.Equal(1, calls)
}

[<Theory>]
[<InlineData("0.51", "0.75", "0.30")>]
[<InlineData("0.50", "0.76", "0.30")>]
[<InlineData("0.50", "0.75", "0.56")>]
let ``gate changes affect acceptance and invalidate cache identity`` confidence probability margin = task {
    let handler, layer = provider (fun _ _ -> Task.FromResult(response answer))
    use _handler = handler
    let before = layer.Configure(settings ())
    let after = layer.Configure({settings () with Jev = configuration "3000" confidence probability margin})
    Assert.NotEqual<string>(before.CacheIdentity, after.CacheIdentity)
    let! result = after.Classify(context, CancellationToken.None)
    Assert.Equal(LlmVerdict.Skip(Some "SPAM probability: 0.750", None), result.Verdict)
}

[<Theory>]
[<InlineData("NaN")>]
[<InlineData("Infinity")>]
[<InlineData("-0.01")>]
[<InlineData("1.01")>]
let ``invalid thresholds reject configuration`` value =
    %Assert.Throws<ArgumentException>(fun () -> %(configuration "3000" value "0.75" "0.30"))
