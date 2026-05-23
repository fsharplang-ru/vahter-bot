namespace BotTestInfra

open System

/// Lightweight DTO for inspecting FakeTgApi call logs in tests.
[<CLIMutable>]
type FakeCall =
    { Method: string
      Url: string
      Body: string
      Timestamp: DateTime }

/// Mock payload for FakeTgApi /test/mock/chatMember.
[<CLIMutable>]
type ChatMemberMock =
    { userId: int64
      status: string }

/// Mock payload for FakeTgApi /test/mock/file.
[<CLIMutable>]
type FileMock =
    { fileId: string
      contentBase64: string }

/// Mock payload for FakeAzureOcrApi /test/mock/response.
[<CLIMutable>]
type AzureResponseMock =
    { status: int
      body: string }

/// Mock payload for FakeTgApi /test/mock/methodError.
[<CLIMutable>]
type MethodErrorMock =
    { methodName: string
      enabled: bool }

/// Mock payload for FakeTgApi /test/mock/methodDelay (per-method artificial
/// delay). Used by concurrency-race tests to widen the window between
/// transaction commit and SendMessage so a second webhook can deterministically
/// race past it. delayMs=0 clears the delay.
[<CLIMutable>]
type MethodDelayMock =
    { methodName: string
      delayMs: int }

/// Mock payload for FakeAzureOcrApi /test/mock/delay (per-call response delay).
[<CLIMutable>]
type AzureDelayMock =
    { delayMs: int }

/// Mock payload for FakeAzureOcrApi /test/mock/errorMode.
/// Recognized values: "" (off), "network" (TCP abort), "timeout" (10s stall).
[<CLIMutable>]
type AzureErrorModeMock =
    { mode: string }

/// Single scripted response. errorMode "" means use status+body normally.
[<CLIMutable>]
type AzureScriptedResponse =
    { status: int
      body: string
      delayMs: int
      errorMode: string }

[<CLIMutable>]
type AzureScriptMock =
    { responses: AzureScriptedResponse array }
