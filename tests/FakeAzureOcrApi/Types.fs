namespace FakeAzureOcrApi

open System

[<CLIMutable>]
type ApiCallLog =
    { Method: string
      Url: string
      Body: string
      Timestamp: DateTime }

[<CLIMutable>]
type ResponseMock =
    { status: int
      body: string }

[<CLIMutable>]
type ResponseMockDto =
    { status: int
      body: string }

/// Per-request delay (ms) applied to /computervision/imageanalysis:analyze.
/// 0 disables the delay.
[<CLIMutable>]
type DelayMockDto =
    { delayMs: int }

/// Per-request error mode. "" disables. Recognized values:
///   "network"  → fake aborts the connection (HttpRequestException on client)
///   "timeout"  → fake stalls 10s so the client's HttpClient.Timeout fires
[<CLIMutable>]
type ErrorModeDto =
    { mode: string }

/// One scripted response. Status 599 with empty body is a sentinel for
/// "use errorMode" — when present, the fake aborts/stalls per the mode.
[<CLIMutable>]
type ScriptedResponse =
    { status: int
      body: string
      delayMs: int
      errorMode: string }

[<CLIMutable>]
type ResponseScriptDto =
    { responses: ScriptedResponse array }

/// Global knobs for the chat-completions streaming (SSE) mode. All zeros = defaults:
///   chunkDelayMs      — artificial delay before each SSE data line
///   abortAfterChunks  — reset the connection after N data lines were written
///                       (combine with chunkDelayMs so earlier chunks reach the client)
///   retryAfterSeconds — adds a Retry-After header to scripted 429 responses
[<CLIMutable>]
type LlmStreamOptionsDto =
    { chunkDelayMs: int
      abortAfterChunks: int
      retryAfterSeconds: int }

