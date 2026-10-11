module VahterBanBot.Tests.LlmVerdictCacheGlobalFlagTests

open VahterBanBot.Tests.ContainerTestBase
open VahterBanBot.LlmVerdictCache
open BotTestInfra
open Xunit

type LlmVerdictCacheGlobalFlagTests(fixture: LlmVerdictCacheGlobalDisabledTestContainers, _ml: MlAwaitFixture) =

    let resetFakes () = task {
        do! fixture.ClearAzureOcrCalls()
        do! fixture.ClearFakeCalls()
        do! fixture.ClearLlmVerdictCache()
    }

    [<Fact>]
    let ``LLM verdict cache global flag off: two different senders' identical SPAM text are each classified independently`` () = task {
        do! resetFakes ()
        let a = Tg.user(firstName = "kill flagoff-a")
        let b = Tg.user(firstName = "kill flagoff-b")

        let m1 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = a)
        let! _ = fixture.SendMessage m1
        let! v1 = fixture.TryGetLlmTriageVerdict m1.Message.Value
        Assert.Equal(Some "SPAM", v1)

        let m2 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = b)
        let! _ = fixture.SendMessage m2
        let! v2 = fixture.TryGetLlmTriageVerdict m2.Message.Value
        Assert.Equal(Some "SPAM", v2)

        // Flag off ⇒ per-sender only, even for SPAM — no cross-sender dedup, one Azure call each.
        let! calls = fixture.GetAzureLlmCalls()
        Assert.Equal(2, calls.Length)
    }

    [<Fact>]
    let ``LLM verdict cache global flag off: the same sender's repeated identical text is still cached`` () = task {
        do! resetFakes ()
        let a = Tg.user(firstName = "kill flagoff-repeat")

        let m1 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = a)
        let! _ = fixture.SendMessage m1
        let m2 = Tg.quickMsg(chat = fixture.ChatsToMonitor[1], text = "77", from = a)
        let! _ = fixture.SendMessage m2

        let! d1 = fixture.MessageIsAutoDeleted m1.Message.Value
        let! d2 = fixture.MessageIsAutoDeleted m2.Message.Value
        Assert.True(d1, "first copy deleted")
        Assert.True(d2, "second (cached) copy still deleted from the cached per-sender verdict")

        let! calls = fixture.GetAzureLlmCalls()
        Assert.Equal(1, calls.Length)

        let! cacheHit = fixture.TryGetLlmVerdictCacheHit m2.Message.Value
        Assert.Equal(Some ("SPAM", Some "keyword match: kill", "sender"), cacheHit)

        let! sends = fixture.GetFakeCalls "sendMessage"
        let toDetected = sends |> Array.filter (fun c -> c.Body.Contains $"\"chat_id\":{fixture.DetectedSpamChannel.Id}")
        Assert.True(toDetected |> Array.exists (fun c -> c.Body.Contains "cached/sender"), "sender-tier cache hit must be marked in the channel text")
    }

    [<Fact>]
    let ``LLM verdict cache global flag off: repeated NOT_SPAM is reclassified`` () = task {
        do! resetFakes ()
        let sender = Tg.user()
        let text = "77"
        let cache = LlmVerdictCacheRepository(fixture.DbConnectionString) :> ILlmVerdictCache
        do! cache.Save(senderKey sender.Id text, "NOT_SPAM", None, None)
        for chat in fixture.ChatsToMonitor |> List.take 2 do
            let msg = Tg.quickMsg(chat = chat, text = text, from = sender)
            let! _ = fixture.SendMessage msg
            let! deleted = fixture.MessageIsAutoDeleted msg.Message.Value
            Assert.False(deleted)
        let! calls = fixture.GetAzureLlmCalls()
        Assert.Equal(2, calls.Length)
    }

    interface IClassFixture<MlAwaitFixture>
