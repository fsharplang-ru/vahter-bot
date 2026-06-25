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

