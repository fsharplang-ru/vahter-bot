namespace FakeAzureOcrApi

open System
open System.Collections.Concurrent

module Store =
    let defaultOcrResponse = """{"modelVersion":"2023-10-01","metadata":{"width":1020,"height":638},"readResult":{"blocks":[{"lines":[{"text":"b","boundingPolygon":[{"x":1,"y":24},{"x":1005,"y":27},{"x":1004,"y":377},{"x":0,"y":371}],"words":[{"text":"b","confidence":0.9}]}]}]}}"""

    let calls = ConcurrentQueue<ApiCallLog>()
    let mutable responseStatus = 200
    let mutable responseBody = defaultOcrResponse

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

