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
