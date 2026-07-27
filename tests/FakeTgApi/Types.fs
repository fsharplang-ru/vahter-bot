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

/// Mock payload for FakeTgApi /test/mock/rejectMdv2 (AlitaBot Slice 6) — when enabled,
/// sendMessage/editMessageText calls carrying `"parse_mode":"MarkdownV2"` get a simulated
/// 400 "can't parse entities" instead of the normal ok response, so the fake suite can
/// exercise Mdv2Delivery's plain-text fallback without a real MDV2-rejecting payload
/// (the fake never actually validates entities/parse_mode otherwise).
[<CLIMutable>]
type RejectMdv2Mock = { enabled: bool }

