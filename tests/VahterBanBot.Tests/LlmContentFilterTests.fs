module VahterBanBot.Tests.LlmContentFilterTests

open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

/// Azure OpenAI's RAI policy can reject the LLM-triage prompt outright with HTTP 400, error
/// code "content_filter", when the (already ML-flagged) message text is classified as severely
/// harmful — see incident aea4d561848519ad948d5d54eaea1b38 (2026-08-12): a spam photo whose
/// OCR-appended text advertised CSAM categories. That is now LlmVerdict.ContentFiltered, which
/// Bot.fs's GetAutoVerdict maps to AutoVerdict.ContentFilterSpam (a DISTINCT case from
/// AutoVerdict.Spam — same enforcement, different human-facing rendering) when
/// LLM_CONTENT_FILTER_IS_SPAM=true (default) — see LlmTriage.fs's ClientResultException handler
/// and Types.fs's LlmVerdict/AutoVerdict/AutoDeleteReason.
///
/// The scripted 400 response body below mirrors the real Azure shape (`error.code` +
/// `error.innererror.content_filter_result`, ONE category — "sexual" — actually filtered=true,
/// the rest false), so it exercises the same raw-body-parsing path production uses
/// (`isContentFilterRejection` preferring `ex.GetRawResponse()` over a message substring match,
/// and `extractContentFilterTriggers` rendering "sexual=high" as the only trigger).
let private contentFilterBody =
    """{"error":{"message":"The response was filtered due to the prompt triggering Azure OpenAI's content management policy.","type":null,"param":"prompt","code":"content_filter","status":400,"innererror":{"code":"ResponsibleAIPolicyViolation","content_filter_result":{"sexual":{"filtered":true,"severity":"high"},"violence":{"filtered":false,"severity":"safe"},"hate":{"filtered":false,"severity":"safe"},"self_harm":{"filtered":false,"severity":"safe"},"jailbreak":{"filtered":false,"detected":false}}}}}"""

let private contentFilter400: AzureScriptedResponse =
    { status = 400; body = contentFilterBody; delayMs = 0; errorMode = "" }

let private leaksToActionChannel (potentialSpamChannelId: int64) (sends: FakeCall array) =
    sends |> Array.filter (fun c -> c.Body.Contains $"\"chat_id\":{potentialSpamChannelId}")

let private toChannel (channelId: int64) (sends: FakeCall array) =
    sends |> Array.filter (fun c -> c.Body.Contains $"\"chat_id\":{channelId}")

/// LLM_CONTENT_FILTER_IS_SPAM defaults to true — shares the ML-enabled container with
/// LlmTriageTests/LlmTriageResilienceTests.
type LlmContentFilterTests(fixture: MlEnabledVahterTestContainers, _ml: MlAwaitFixture) =

    [<Fact>]
    let ``LLM triage content_filter 400 is treated as SPAM and deleted (default LLM_CONTENT_FILTER_IS_SPAM=true)`` () = task {
        do! fixture.ClearAzureOcrCalls()
        do! fixture.ClearFakeCalls()
        do! fixture.SetAzureLlmScript [| contentFilter400 |]
        do! fixture.ClearLlmVerdictCache()

        // "77" scores in the ML warning band (see LlmTriageTests) — triggers LLM triage.
        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77")
        let! _ = fixture.SendMessage msg

        let! deleted = fixture.MessageIsAutoDeleted msg.Message.Value
        Assert.True(deleted, "content_filter rejection should be treated as SPAM and auto-deleted (flag ON)")

        // Only the initial (400, non-retryable) call — content_filter must not trigger a retry loop.
        let! calls = fixture.GetAzureLlmCalls()
        Assert.Equal(1, calls.Length)

        // A content_filter-driven deletion is a real action, not a leak to the human action channel.
        let! sends = fixture.GetFakeCalls "sendMessage"
        Assert.Empty(leaksToActionChannel fixture.PotentialSpamChannel.Id sends)

        // The human-facing rendering (formatReasonStr via AutoDeleteReason.ContentFilterSpam) must
        // say this was Azure's content policy filter, not a plain ML/LLM SPAM verdict, AND must
        // include which category triggered it ("sexual=high" — the only filtered=true entry in the
        // scripted body above). Checked on the AllLogs mirror, which every DeleteSpam call posts to.
        let allLogsSends = sends |> toChannel fixture.AllLogsChannel.Id
        Assert.Contains(allLogsSends, fun c -> c.Body.Contains "Azure content policy filter" && c.Body.Contains "sexual=high")
    }

    interface IClassFixture<MlAwaitFixture>

/// LLM_CONTENT_FILTER_IS_SPAM forced off — content_filter rejections must fall back to the
/// pre-fix Uncertain/human-review behavior instead of SPAM.
type LlmContentFilterDisabledTests(fixture: LlmContentFilterDisabledTestContainers, _ml: MlAwaitFixture) =

    [<Fact>]
    let ``LLM triage content_filter 400 falls back to Uncertain when LLM_CONTENT_FILTER_IS_SPAM=false`` () = task {
        do! fixture.ClearAzureOcrCalls()
        do! fixture.ClearFakeCalls()
        do! fixture.SetAzureLlmScript [| contentFilter400 |]
        do! fixture.ClearLlmVerdictCache()

        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77")
        let! _ = fixture.SendMessage msg

        let! deleted = fixture.MessageIsAutoDeleted msg.Message.Value
        Assert.False(deleted, "flag OFF ⇒ content_filter falls back to Uncertain, not auto-deleted")

        // Uncertain always reports to the human action channel (same as a plain Error verdict).
        let! sends = fixture.GetFakeCalls "sendMessage"
        Assert.NotEmpty(leaksToActionChannel fixture.PotentialSpamChannel.Id sends)
    }

    interface IClassFixture<MlAwaitFixture>
