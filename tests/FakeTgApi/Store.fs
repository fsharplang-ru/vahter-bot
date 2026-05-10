namespace FakeTgApi

open System
open System.Collections.Concurrent
open System.Threading

module Store =
    let calls = ConcurrentQueue<ApiCallLog>()
    let chatMemberStatus = ConcurrentDictionary<int64, string>()
    let files = ConcurrentDictionary<string, byte[]>()
    let methodErrors = ConcurrentDictionary<string, bool>()

    /// Monotonic counter for synthesizing message_ids on send-* responses. Real Telegram
    /// returns a distinct message_id for every send; using a constant breaks any test that
    /// relies on per-message cleanup (e.g. ExpireCallbacksByMessageId).
    let private nextMessageId = ref 1000
    let allocMessageId () =
        Interlocked.Increment(nextMessageId)

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

