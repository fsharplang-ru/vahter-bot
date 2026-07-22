namespace FakeAzureOcrApi

open System
open System.Collections.Concurrent

module Store =
    let defaultOcrResponse = """{"modelVersion":"2023-10-01","metadata":{"width":1020,"height":638},"readResult":{"blocks":[{"lines":[{"text":"b","boundingPolygon":[{"x":1,"y":24},{"x":1005,"y":27},{"x":1004,"y":377},{"x":0,"y":371}],"words":[{"text":"b","confidence":0.9}]}]}]}}"""

    let calls = ConcurrentQueue<ApiCallLog>()
    let mutable responseStatus = 200
    let mutable responseBody = defaultOcrResponse
    let mutable responseDelayMs = 0
    let mutable responseErrorMode = ""

    /// Scripted responses. If non-empty, dequeue one per OCR call. After empty,
    /// fall back to the static responseStatus/responseBody. Used to test
    /// "first call fails, second call succeeds" retry semantics.
    let responseScript = ConcurrentQueue<ScriptedResponse>()

    /// Scripted responses for the Azure OpenAI chat-completions endpoint (separate from the
    /// OCR queue so the two don't interfere). If non-empty, dequeue one per LLM call; after it
    /// empties, fall back to the normal keyword-routed 200. Used to inject HTTP 429s so tests
    /// can exercise the bot's retry/backoff and the "leak to action channel on failure" behavior.
    let llmResponseScript = ConcurrentQueue<ScriptedResponse>()

    /// Scripted responses for the REACTION-triage chat-completions calls (json_schema
    /// `reaction_spam_verdict`). Kept separate from `llmResponseScript` so a text-triage script can
    /// never be consumed by a reaction call and vice versa. Used to inject HTTP 429s and assert the
    /// reaction path fails fast (no retry) instead of storming.
    let reactionLlmResponseScript = ConcurrentQueue<ScriptedResponse>()

    /// Scripted responses for the Azure OpenAI audio/transcriptions endpoint (AlitaBot voice
    /// transcription). Kept separate from the chat-completions queues. If non-empty, dequeue one
    /// per call; after it empties, respond with an empty transcript ({"text":""}) so a forgotten
    /// script doesn't silently reuse a stale scripted transcript across tests.
    let sttResponseScript = ConcurrentQueue<ScriptedResponse>()

    /// A tiny (1x1, transparent) but genuinely valid PNG, base64-encoded — the default
    /// "generated image" for the images/generations and images/edits fakes below.
    let tinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="

    let defaultImageResponse =
        $"""{{"created":1774736361,"data":[{{"b64_json":"{tinyPngBase64}"}}],"usage":{{"input_tokens":10,"output_tokens":1000,"total_tokens":1010}}}}"""

    /// Scripted responses for the Azure OpenAI images/generations + images/edits endpoints
    /// (AlitaBot image generation, S3). Kept separate from the other queues. If non-empty,
    /// dequeue one per call (either endpoint); after it empties, both endpoints fall back to
    /// `defaultImageResponse` (a scripted tiny PNG) rather than an error.
    let imageResponseScript = ConcurrentQueue<ScriptedResponse>()

    /// Scripted responses for the Azure OpenAI embeddings endpoint (AlitaBot's memory
    /// foundation, Slice 5a). Kept separate from the other queues. If non-empty, dequeue
    /// one per call; after it empties, calls fall back to Embedding.embed's deterministic
    /// hash-of-text vectors — the common case, since most embedding tests want
    /// similarity to be a function of the input text, not a scripted value.
    let embeddingsResponseScript = ConcurrentQueue<ScriptedResponse>()

    /// Streaming knobs for the chat-completions SSE mode (see LlmStreamOptionsDto).
    /// Set via /test/mock/azure-llm-stream-options; all zeros = defaults.
    let mutable llmStreamChunkDelayMs = 0
    let mutable llmStreamAbortAfterChunks = 0
    let mutable llmRetryAfterSeconds = 0

    let logCall (methodName: string) (url: string) (body: string) =
        calls.Enqueue(
            { Method = methodName
              Url = url
              Body = body
              Timestamp = DateTime.UtcNow }
        )

    let clearCalls () =
        let mutable item = Unchecked.defaultof<ApiCallLog>
        while calls.TryDequeue(&item) do
            ()

    let clearScript () =
        let mutable item = Unchecked.defaultof<ScriptedResponse>
        while responseScript.TryDequeue(&item) do
            ()

    let clearLlmScript () =
        let mutable item = Unchecked.defaultof<ScriptedResponse>
        while llmResponseScript.TryDequeue(&item) do
            ()

    let clearReactionLlmScript () =
        let mutable item = Unchecked.defaultof<ScriptedResponse>
        while reactionLlmResponseScript.TryDequeue(&item) do
            ()

    let clearSttScript () =
        let mutable item = Unchecked.defaultof<ScriptedResponse>
        while sttResponseScript.TryDequeue(&item) do
            ()

    let clearImageScript () =
        let mutable item = Unchecked.defaultof<ScriptedResponse>
        while imageResponseScript.TryDequeue(&item) do
            ()

    let clearEmbeddingsScript () =
        let mutable item = Unchecked.defaultof<ScriptedResponse>
        while embeddingsResponseScript.TryDequeue(&item) do
            ()

    /// Resets the OCR mock to its pristine baseline: default 200 response, no delay,
    /// no error mode, and an empty script queue. Tests share one fake across the whole
    /// assembly, so a test that set a custom response/errorMode must not leak it to the
    /// next test (which would otherwise read stale state — see #163 review). The OpenAI
    /// LLM script is intentionally left untouched (a separate queue).
    let resetOcrMock () =
        responseStatus <- 200
        responseBody <- defaultOcrResponse
        responseDelayMs <- 0
        responseErrorMode <- ""
        clearScript ()

