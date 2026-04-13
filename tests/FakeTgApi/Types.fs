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

