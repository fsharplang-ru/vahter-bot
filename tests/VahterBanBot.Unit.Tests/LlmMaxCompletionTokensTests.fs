/// Regression coverage for the gpt-5.x hotfix: prod LLM triage failed every call with
/// HTTP 400 "Unsupported parameter: 'max_tokens' is not supported with this model. Use
/// 'max_completion_tokens' instead." — Azure.AI.OpenAI (2.9.0-beta.1) serializes
/// `ChatCompletionOptions.MaxOutputTokenCount` as legacy `max_tokens` unless
/// `SetNewMaxCompletionTokensPropertyEnabled(true)` is called on the options.
///
/// `AzureChatClient.RefreshMaxTokenSerialization` (internal) is the method the SDK's
/// request pipeline calls to apply that opt-in just before serializing the wire body —
/// there's no public API to observe the effect otherwise, so this test invokes it via
/// reflection to reproduce exactly what a real `CompleteChatAsync` call does.
#nowarn "57"
module VahterBanBot.Unit.Tests.LlmMaxCompletionTokensTests

open System
open System.ClientModel.Primitives
open System.Reflection
open Azure.AI.OpenAI.Chat
open OpenAI.Chat
open Xunit

let private serializedBody (options: ChatCompletionOptions) =
    let model = options :> IJsonModel<ChatCompletionOptions>
    let bytes = model.Write(ModelReaderWriterOptions("W"))
    Text.Encoding.UTF8.GetString(bytes.ToArray())

/// Simulates the SDK's request pipeline applying the max-tokens opt-in, the same step
/// `AzureChatClient.CompleteChatAsync` performs internally before sending the request.
let private applyAzureMaxTokenSerialization (options: ChatCompletionOptions) =
    let clientTy = typeof<Azure.AI.OpenAI.AzureOpenAIClient>.Assembly.GetType("Azure.AI.OpenAI.Chat.AzureChatClient")
    let refreshMethod =
        clientTy.GetMethod(
            "RefreshMaxTokenSerialization",
            BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static)
    let args : obj[] = [| box options |]
    refreshMethod.Invoke(null, args) |> ignore
    args.[0] :?> ChatCompletionOptions

[<Fact>]
let ``without the opt-in, Azure SDK would serialize legacy max_tokens (documents the bug)`` () =
    let options = ChatCompletionOptions(MaxOutputTokenCount = Nullable 100)
    let body = applyAzureMaxTokenSerialization options |> serializedBody
    Assert.Contains("\"max_tokens\"", body)
    Assert.DoesNotContain("\"max_completion_tokens\"", body)

[<Fact>]
let ``SetNewMaxCompletionTokensPropertyEnabled(true) makes Azure SDK serialize max_completion_tokens`` () =
    let options = ChatCompletionOptions(MaxOutputTokenCount = Nullable 100)
    options.SetNewMaxCompletionTokensPropertyEnabled(true)
    let body = applyAzureMaxTokenSerialization options |> serializedBody
    Assert.Contains("\"max_completion_tokens\"", body)
    Assert.DoesNotContain("\"max_tokens\"", body)
