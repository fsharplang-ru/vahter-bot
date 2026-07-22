namespace FakeTgApi

open System

[<CLIMutable>]
type ApiCallLog =
    { Method: string
      Url: string
      Body: string
      Timestamp: DateTime }

[<CLIMutable>]
type ChatMemberMock =
    { userId: int64
      status: string } // "member" | "left" | "kicked" | "administrator"

[<CLIMutable>]
type FileMock =
    { fileId: string
      contentBase64: string }

[<CLIMutable>]
type MethodErrorMock =
    { methodName: string
      enabled: bool }

[<CLIMutable>]
type MethodDelayMock =
    { methodName: string
      delayMs: int }

/// Mock payload for FakeTgApi /test/mock/chat — registers a username->chat
/// resolution so getChat("@username") returns a meaningful id/title.
[<CLIMutable>]
type ChatMock =
    { username: string
      id: int64
      title: string }

