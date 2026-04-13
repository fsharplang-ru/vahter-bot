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

