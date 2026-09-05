module VahterBanBot.Tests.LlmVerdictCacheTieredTtlTests

open System
open System.Net
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

/// D1 (length-tiered TTL) + D2 (invalidate) + D5 (normalized keys) black-box tests. Text bodies
/// are repeated "77" tokens — the only content confirmed to land in the test ML model's warning band.
type LlmVerdictCacheTieredTtlTests(fixture: MlEnabledVahterTestContainers, _ml: MlAwaitFixture) =

    let longSpamText = "77 77 77 77 77 77 77 77 77 77 77 77 77 77"
    let longSkipText = "77 77 77 77 77 77 77 77 77 77 77 77 77 77 77"

    let resetFakes () = task {
        do! fixture.ClearAzureOcrCalls()
        do! fixture.ClearFakeCalls()
        do! fixture.SetAzureLlmScript [||]
        do! fixture.ClearLlmVerdictCache()
    }

    [<Fact>]
    let ``LLM verdict cache: long SPAM text aged past the short TTL is still a hit (long TTL applies)`` () = task {
        do! resetFakes ()
        let a = Tg.user(firstName = "kill long-ttl-hit-a")
        let b = Tg.user(firstName = "kill long-ttl-hit-b")

        let m1 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = longSpamText, from = a)
        let! _ = fixture.SendMessage m1
        let! d1 = fixture.MessageIsAutoDeleted m1.Message.Value
        Assert.True(d1, "first copy should be deleted (SPAM)")
        let! calls1 = fixture.GetAzureLlmCalls()
        Assert.Equal(1, calls1.Length)

        // past short TTL (60 min), within long TTL (10080 min)
        do! fixture.AgeLlmVerdictCache(TimeSpan.FromMinutes 61.0)

        let m2 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = longSpamText, from = b)
        let! _ = fixture.SendMessage m2
        let! d2 = fixture.MessageIsAutoDeleted m2.Message.Value
        Assert.True(d2, "second sender's identical long text should still be deleted from the (still fresh) long-TTL cache entry")

        let! calls2 = fixture.GetAzureLlmCalls()
        Assert.Equal(1, calls2.Length)

        let! cacheHit = fixture.TryGetLlmVerdictCacheHit m2.Message.Value
        Assert.Equal(Some ("SPAM", Some "keyword match: kill", "global"), cacheHit)
    }

    [<Fact>]
    let ``LLM verdict cache: long SPAM text aged past the long TTL is re-classified`` () = task {
        do! resetFakes ()
        let a = Tg.user(firstName = "kill long-ttl-expire-a")
        let b = Tg.user(firstName = "kill long-ttl-expire-b")

        let m1 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = longSpamText, from = a)
        let! _ = fixture.SendMessage m1
        let! calls1 = fixture.GetAzureLlmCalls()
        Assert.Equal(1, calls1.Length)

        // past long TTL (10080 min = 7 days)
        do! fixture.AgeLlmVerdictCache(TimeSpan.FromMinutes 10081.0)

        let m2 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = longSpamText, from = b)
        let! _ = fixture.SendMessage m2
        let! v2 = fixture.TryGetLlmTriageVerdict m2.Message.Value
        Assert.Equal(Some "SPAM", v2)

        let! calls2 = fixture.GetAzureLlmCalls()
        Assert.Equal(2, calls2.Length)

        let! cacheHit = fixture.TryGetLlmVerdictCacheHit m2.Message.Value
        Assert.Equal(None, cacheHit)
    }

    [<Fact>]
    let ``LLM verdict cache: long SKIP text aged past the short TTL is re-classified (SKIP never gets the long TTL)`` () = task {
        do! resetFakes ()
        let a = Tg.user(firstName = "spam long-skip-ttl-a")
        let b = Tg.user(firstName = "spam long-skip-ttl-b")

        let m1 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = longSkipText, from = a)
        let! _ = fixture.SendMessage m1
        let! v1 = fixture.TryGetLlmTriageVerdict m1.Message.Value
        Assert.Equal(Some "SKIP", v1)
        let! calls1 = fixture.GetAzureLlmCalls()
        Assert.Equal(1, calls1.Length)

        do! fixture.AgeLlmVerdictCache(TimeSpan.FromMinutes 61.0)

        let m2 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = longSkipText, from = b)
        let! _ = fixture.SendMessage m2
        let! v2 = fixture.TryGetLlmTriageVerdict m2.Message.Value
        Assert.Equal(Some "SKIP", v2)

        let! calls2 = fixture.GetAzureLlmCalls()
        Assert.Equal(2, calls2.Length)

        let! cacheHit = fixture.TryGetLlmVerdictCacheHit m2.Message.Value
        Assert.Equal(None, cacheHit)
    }

    [<Fact>]
    let ``LLM verdict cache: whitespace variant of a cached text is a hit (D5 normalization)`` () = task {
        do! resetFakes ()
        let a = Tg.user(firstName = "kill norm-variant-a")
        let b = Tg.user(firstName = "kill norm-variant-b")

        // internal whitespace differs only — normalizes to the same cache key (D5)
        let baseText    = longSpamText
        let variantText = "77  77 77 77 77 77 77 77 77 77 77 77 77 77"

        let m1 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = baseText, from = a)
        let! _ = fixture.SendMessage m1
        let! d1 = fixture.MessageIsAutoDeleted m1.Message.Value
        Assert.True(d1, "first copy should be deleted (SPAM)")
        let! calls1 = fixture.GetAzureLlmCalls()
        Assert.Equal(1, calls1.Length)

        let m2 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = variantText, from = b)
        let! _ = fixture.SendMessage m2
        let! d2 = fixture.MessageIsAutoDeleted m2.Message.Value
        Assert.True(d2, "the whitespace variant should hash to the same normalized key and hit the cache")

        let! calls2 = fixture.GetAzureLlmCalls()
        Assert.Equal(1, calls2.Length)

        let! cacheHit = fixture.TryGetLlmVerdictCacheHit m2.Message.Value
        Assert.Equal(Some ("SPAM", Some "keyword match: kill", "global"), cacheHit)
    }

    [<Fact>]
    let ``LLM verdict cache: ham-mark of an LLM-deleted message invalidates the global row, forcing a fresh Azure call`` () = task {
        do! resetFakes ()
        let a = Tg.user(firstName = "kill ham-invalidate-a")
        let b = Tg.user(firstName = "kill ham-invalidate-b")
        let text = longSpamText

        let m1 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = text, from = a)
        let! _ = fixture.SendMessage m1
        let! d1 = fixture.MessageIsAutoDeleted m1.Message.Value
        Assert.True(d1, "sanity: message should have been auto-deleted as spam")
        let! calls1 = fixture.GetAzureLlmCalls()
        Assert.Equal(1, calls1.Length)

        let! callbackId = fixture.GetCallbackId m1.Message.Value "NotASpam"
        let! _ = fixture.SendMessage(Tg.callback(string callbackId, from = fixture.Vahters[0]))

        let m2 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = text, from = b)
        let! _ = fixture.SendMessage m2

        let! calls2 = fixture.GetAzureLlmCalls()
        Assert.Equal(2, calls2.Length)

        let! cacheHit = fixture.TryGetLlmVerdictCacheHit m2.Message.Value
        Assert.Equal(None, cacheHit)
    }

    [<Fact>]
    let ``LLM verdict cache: /ban invalidates both the per-sender NOT_SPAM row and the global SKIP row`` () = task {
        do! resetFakes ()
        let x = Tg.user()
        let y = Tg.user(firstName = "spam ban-inval-y")
        let text = longSpamText

        let m1 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = text, from = x)
        let! _ = fixture.SendMessage m1
        let! v1 = fixture.TryGetLlmTriageVerdict m1.Message.Value
        Assert.Equal(Some "NOT_SPAM", v1)

        let m2 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = text, from = y)
        let! _ = fixture.SendMessage m2
        let! v2 = fixture.TryGetLlmTriageVerdict m2.Message.Value
        Assert.Equal(Some "SKIP", v2)
        let! calls2 = fixture.GetAzureLlmCalls()
        Assert.Equal(2, calls2.Length)

        let! banResp = Tg.replyMsg(m1.Message.Value, "/ban", fixture.Vahters[0]) |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)

        // harness only — a banned sender never reaches LLM triage, /unban itself touches nothing
        let! _ = Tg.quickMsg(text = $"/unban {x.Id}", chat = fixture.ChatsToMonitor[0], from = fixture.Vahters[0]) |> fixture.SendMessage

        let m3 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = text, from = x)
        let! _ = fixture.SendMessage m3

        let! calls3 = fixture.GetAzureLlmCalls()
        Assert.Equal(3, calls3.Length)
        let! cacheHit = fixture.TryGetLlmVerdictCacheHit m3.Message.Value
        Assert.Equal(None, cacheHit)
    }

    [<Fact>]
    let ``LLM verdict cache: /vahter markspam invalidates both the per-sender NOT_SPAM row and the global SKIP row`` () = task {
        do! resetFakes ()
        let x = Tg.user()
        let y = Tg.user(firstName = "spam markspam-inval-y")
        let text = longSpamText

        let m1 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = text, from = x)
        let! _ = fixture.SendMessage m1
        let! v1 = fixture.TryGetLlmTriageVerdict m1.Message.Value
        Assert.Equal(Some "NOT_SPAM", v1)

        let m2 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = text, from = y)
        let! _ = fixture.SendMessage m2
        let! v2 = fixture.TryGetLlmTriageVerdict m2.Message.Value
        Assert.Equal(Some "SKIP", v2)
        let! calls2 = fixture.GetAzureLlmCalls()
        Assert.Equal(2, calls2.Length)

        // ref token must point at the REAL m1 — TryGetMessageTextAndSender reads snapshot_message
        let logText = $"Deleted spam (ml) in @pro.hell from @bad with text:\n{text}\n#ref:{m1.Message.Value.Chat.Id}:{m1.Message.Value.MessageId}"
        let logMsg = Tg.quickMsg(text = logText, chat = fixture.AdminChannel, from = fixture.Vahters[0])
        let! resp = Tg.replyMsg(logMsg.Message.Value, "/vahter markspam", fixture.Vahters[0]) |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let m3 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = text, from = x)
        let! _ = fixture.SendMessage m3

        let! calls3 = fixture.GetAzureLlmCalls()
        Assert.Equal(3, calls3.Length)
        let! cacheHit = fixture.TryGetLlmVerdictCacheHit m3.Message.Value
        Assert.Equal(None, cacheHit)
    }

    interface IClassFixture<MlAwaitFixture>
