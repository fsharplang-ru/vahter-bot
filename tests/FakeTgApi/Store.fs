namespace FakeTgApi

open System
open System.Collections.Concurrent

module Store =
    let calls = ConcurrentQueue<ApiCallLog>()
    let chatMemberStatus = ConcurrentDictionary<int64, string>()
    let files = ConcurrentDictionary<string, byte[]>()
    let methodErrors = ConcurrentDictionary<string, bool>()

    let logCall (methodName: string) (url: string) (body: string) =
        calls.Enqueue(
            { Method = methodName
              Url = url
              Body = body
              Timestamp = DateTime.UtcNow }
        )

    let clearCalls () =
        // ConcurrentQueue has no Clear(), so we drain it.
        let mutable item = Unchecked.defaultof<ApiCallLog>
        while calls.TryDequeue(&item) do
            ()
        methodErrors.Clear()

