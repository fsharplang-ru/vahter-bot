module VahterBanBot.Tests.LlmTriageResilienceTests

open System
open System.Threading.Tasks
open System.Text.Json
open VahterBanBot.LlmVerdictCache
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

type LlmTriageResilienceTests(fixture: MlEnabledVahterTestContainers, _ml: MlAwaitFixture) =

    // One scripted Azure rate-limit response. The bot does not parse a non-2xx body, so its
    // contents are irrelevant; only the 429 status matters.
    let http429: AzureScriptedResponse =
        { status = 429; body = """{"error":{"code":"429","message":"rate limited"}}"""; delayMs = 0; errorMode = "" }

    // Resets recorded fake calls (Azure + Telegram), clears any leftover LLM 429 script, and wipes
    // the persisted verdict cache, so each test starts clean and call-count / cache-hit assertions
    // only observe this test's traffic (the global SPAM/SKIP tier means a stale entry from an
    // earlier test using the same literal text would otherwise leak in as a false cache hit).
    let resetFakes () = task {
        do! fixture.ClearAzureOcrCalls()     // clears ALL recorded fake-Azure calls (OCR + LLM)
        do! fixture.ClearFakeCalls()         // clears recorded fake-Telegram calls
        do! fixture.SetAzureLlmScript [||]   // clears any queued LLM scripted responses
        do! fixture.ClearLlmVerdictCache()   // clears the persisted LLM verdict cache
    }

    let leaksToActionChannel (sends: FakeCall array) =
        sends |> Array.filter (fun c -> c.Body.Contains $"\"chat_id\":{fixture.PotentialSpamChannel.Id}")

    // ── Happy path ────────────────────────────────────────────────────────────

    [<Fact>]
    let ``LLM triage happy path makes exactly one Azure call and deletes the spam`` () = task {
        do! resetFakes ()
        let spammer = Tg.user(firstName = "kill happy-single")
        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = spammer)
        let! _ = fixture.SendMessage msg

        let! verdict = fixture.TryGetLlmTriageVerdict msg.Message.Value
        Assert.Equal(Some "SPAM", verdict)
        let! deleted = fixture.MessageIsAutoDeleted msg.Message.Value
        Assert.True(deleted, "Spam message should be auto-deleted")
        let! calls = fixture.GetAzureLlmCalls()
        Assert.Equal(1, calls.Length)
    }

    // ── Cache (temporal dedup) ────────────────────────────────────────────────

    [<Fact>]
    let ``LLM triage caches verdict: identical spam from one user across channels makes ONE Azure call`` () = task {
        // The reported bug: the same spam posted to N channels = N Azure calls (the later ones 429
        // and leak to the action channel). The verdict cache collapses repeats to a single call.
        // On master this records 2 Azure calls; with the cache it is 1.
        do! resetFakes ()
        let spammer = Tg.user(firstName = "kill dup-across-chats")

        let m1 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = spammer)
        let! _ = fixture.SendMessage m1
        let! d1 = fixture.MessageIsAutoDeleted m1.Message.Value
        Assert.True(d1, "first copy deleted")

        let m2 = Tg.quickMsg(chat = fixture.ChatsToMonitor[1], text = "77", from = spammer)
        let! _ = fixture.SendMessage m2
        let! d2 = fixture.MessageIsAutoDeleted m2.Message.Value
        Assert.True(d2, "second (cached) copy still deleted from the cached verdict")

        let! calls = fixture.GetAzureLlmCalls()
        Assert.Equal(1, calls.Length)

        let! cacheHit = fixture.TryGetLlmVerdictCacheHit m2.Message.Value
        Assert.Equal(Some ("SPAM", Some "keyword match: kill", "global"), cacheHit)
    }

    [<Fact>]
    let ``LLM triage caches SPAM verdict globally: two different senders with identical text make ONE Azure call`` () = task {
        // The measured problem this cache split fixes: a spam campaign spraying identical text
        // across many accounts used to cost one Azure call PER account (the cache key included
        // senderId). SPAM is now cached by text hash alone, so sender B's identical text is served
        // from the cache sender A's classification wrote — and still results in the spam action.
        do! resetFakes ()
        let a = Tg.user(firstName = "kill global-spam-a")
        let b = Tg.user(firstName = "kill global-spam-b")

        let m1 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = a)
        let! _ = fixture.SendMessage m1
        let! v1 = fixture.TryGetLlmTriageVerdict m1.Message.Value
        Assert.Equal(Some "SPAM", v1)
        let! d1 = fixture.MessageIsAutoDeleted m1.Message.Value
        Assert.True(d1, "first sender's spam should be deleted")

        let m2 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = b)
        let! _ = fixture.SendMessage m2
        let! d2 = fixture.MessageIsAutoDeleted m2.Message.Value
        Assert.True(d2, "second (different) sender's identical text is still deleted, from the cached global verdict")

        let! calls = fixture.GetAzureLlmCalls()
        Assert.Equal(1, calls.Length)

        let! cacheHit = fixture.TryGetLlmVerdictCacheHit m2.Message.Value
        Assert.Equal(Some ("SPAM", Some "keyword match: kill", "global"), cacheHit)

        let! sends = fixture.GetFakeCalls "sendMessage"
        let toDetected = sends |> Array.filter (fun c -> c.Body.Contains $"\"chat_id\":{fixture.DetectedSpamChannel.Id}")
        Assert.True(toDetected |> Array.exists (fun c -> c.Body.Contains "cached/global"), "cache-served kill must be marked in the channel text")
    }

    [<Fact>]
    let ``LLM triage caches SKIP verdict globally: two different senders with identical text make ONE Azure call`` () = task {
        // Same global-dedup property as SPAM, for the SKIP verdict: worst case a message goes to
        // human triage that would have gone to human triage anyway, so it's just as safe to share.
        do! resetFakes ()
        let a = Tg.user(firstName = "spam global-skip-a")
        let b = Tg.user(firstName = "spam global-skip-b")

        let m1 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = a)
        let! _ = fixture.SendMessage m1
        let! v1 = fixture.TryGetLlmTriageVerdict m1.Message.Value
        Assert.Equal(Some "SKIP", v1)

        let m2 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = b)
        let! _ = fixture.SendMessage m2
        // SKIP never bans/deletes for either sender — that's the whole point, so assert both stayed
        // untouched instead of re-deriving the verdict for the (uncached, event-less) second message.
        let! d2 = fixture.MessageIsAutoDeleted m2.Message.Value
        Assert.False(d2, "SKIP never auto-deletes")

        let! calls = fixture.GetAzureLlmCalls()
        Assert.Equal(1, calls.Length)

        let! sends = fixture.GetFakeCalls "sendMessage"
        let toPotential = sends |> Array.filter (fun c -> c.Body.Contains $"\"chat_id\":{fixture.PotentialSpamChannel.Id}")
        Assert.True(toPotential |> Array.exists (fun c -> c.Body.Contains "cached/global"), "cache-served SKIP report must be marked in the channel text")
    }

    [<Fact>]
    let ``LLM triage does NOT dedup different senders' NOT_SPAM: a second sender with identical text still gets its own Azure call`` () = task {
        do! resetFakes ()
        let a = Tg.user()
        let b = Tg.user()

        let m1 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = a)
        let! _ = fixture.SendMessage m1
        let! v1 = fixture.TryGetLlmTriageVerdict m1.Message.Value
        Assert.Equal(Some "NOT_SPAM", v1)

        let m2 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = b)
        let! _ = fixture.SendMessage m2
        let! v2 = fixture.TryGetLlmTriageVerdict m2.Message.Value
        Assert.Equal(Some "NOT_SPAM", v2)

        // The safety assertion: sender B's own LlmClassified event exists (i.e. it was actually
        // classified, not silently served from sender A's cached exoneration) → two Azure calls.
        let! calls = fixture.GetAzureLlmCalls()
        Assert.Equal(2, calls.Length)
    }

    [<Fact>]
    let ``LLM triage reassesses repeated cleared messages with updated cross-chat history`` () = task {
        do! resetFakes ()
        let sender = Tg.user()
        let text = String.replicate 17 "77 " |> _.TrimEnd()
        let response verdict : AzureScriptedResponse =
            { status = 200
              body = JsonSerializer.Serialize({|
                  choices = [| {| finish_reason = "stop"; index = 0
                                  message = {| role = "assistant"; content = JsonSerializer.Serialize({| verdict = verdict; reason = "scripted verdict" |}) |} |} |]
                  created = 1774736361; id = "chatcmpl-reassessment"; model = "fake"; ``object`` = "chat.completion"
                  usage = {| completion_tokens = 10; prompt_tokens = 200; total_tokens = 210 |} |})
              delayMs = 0; errorMode = "" }
        do! fixture.SetAzureLlmScript [| response "NOT_SPAM"; response "NOT_SPAM"; response "SPAM" |]
        let messages =
            [| for i in 0..2 -> Tg.quickMsg(chat = fixture.ChatsToMonitor[i % 2], text = text, from = sender) |]
        for i in 0..2 do
            let! _ = fixture.SendMessage messages[i]
            let! deleted = fixture.MessageIsAutoDeleted messages[i].Message.Value
            Assert.Equal((i = 2), deleted)
        let! calls = fixture.GetAzureLlmCalls()
        Assert.Equal(3, calls.Length)
        for i in 0..2 do
            use body = JsonDocument.Parse(calls[i].Body)
            let prompt = body.RootElement.GetProperty("messages").[1].GetProperty("content").GetString()
            Assert.Contains($"Total messages seen from this user: {i} ", prompt)
            if i > 0 then
                Assert.Contains($"Identical message seen earlier: {i} times", prompt)
                Assert.Contains($"Chat {messages[i-1].Message.Value.Chat.Id},", prompt)
    }

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``LLM triage ignores persisted NOT_SPAM entries`` (globalEntry: bool) = task {
        do! resetFakes ()
        let sender = Tg.user(firstName = "kill persisted-clearance")
        let text = "77"
        let cache = LlmVerdictCacheRepository(fixture.DbConnectionString) :> ILlmVerdictCache
        let key = if globalEntry then globalKey text else senderKey sender.Id text
        do! cache.Save(key, "NOT_SPAM", Some "earlier clearance", None)
        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = text, from = sender)
        let! _ = fixture.SendMessage msg
        let! deleted = fixture.MessageIsAutoDeleted msg.Message.Value
        Assert.True(deleted)
        let! calls = fixture.GetAzureLlmCalls()
        Assert.Equal(1, calls.Length)
    }

    [<Fact>]
    let ``LLM triage global cache respects TTL: an aged-out SPAM entry is re-classified`` () = task {
        do! resetFakes ()
        let a = Tg.user(firstName = "kill ttl-a")
        let b = Tg.user(firstName = "kill ttl-b")

        let m1 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = a)
        let! _ = fixture.SendMessage m1
        let! calls1 = fixture.GetAzureLlmCalls()
        Assert.Equal(1, calls1.Length)

        // Age the cache past LLM_VERDICT_CACHE_TTL_MINUTES (60, default/test value) — the global
        // entry from sender A's classification must no longer be honored for sender B.
        do! fixture.AgeLlmVerdictCache(TimeSpan.FromMinutes 61.0)

        let m2 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = b)
        let! _ = fixture.SendMessage m2
        let! v2 = fixture.TryGetLlmTriageVerdict m2.Message.Value
        Assert.Equal(Some "SPAM", v2)

        let! calls2 = fixture.GetAzureLlmCalls()
        Assert.Equal(2, calls2.Length)
    }

    // ── Concurrency (single-flight) ───────────────────────────────────────────

    [<Fact>]
    let ``LLM triage classifies concurrent cleared copies independently`` () = task {
        do! resetFakes ()
        let sender = Tg.user()
        let delayedResponse: AzureScriptedResponse =
            { status = 200
              body = """{"choices":[{"finish_reason":"stop","index":0,"message":{"content":"{\"verdict\":\"NOT_SPAM\",\"reason\":\"no promotion\"}","role":"assistant"}}],"created":1774736361,"id":"chatcmpl-delayed","model":"fake","object":"chat.completion","usage":{"completion_tokens":10,"prompt_tokens":200,"total_tokens":210}}"""
              delayMs = 1000; errorMode = "" }
        do! fixture.SetAzureLlmScript(Array.create 5 delayedResponse)
        let msgs =
            [ for i in 0 .. 4 -> Tg.quickMsg(chat = fixture.ChatsToMonitor[i % 2], text = "77", from = sender) ]
        let! _ = Task.WhenAll(msgs |> List.map (fun m -> fixture.SendMessage m))
        let! calls = fixture.GetAzureLlmCalls()
        Assert.Equal(5, calls.Length)
        for m in msgs do
            let! deleted = fixture.MessageIsAutoDeleted m.Message.Value
            Assert.False(deleted)
    }

    // ── Unhappy path: 429 retry ───────────────────────────────────────────────

    [<Fact>]
    let ``LLM triage retries a 429 then classifies, without leaking to the action channel`` () = task {
        // One scripted 429; the retry falls through to the keyword-routed 200. On master the single
        // 429 becomes an Error verdict and the message leaks to the vahter action channel.
        do! resetFakes ()
        do! fixture.SetAzureLlmScript [| http429 |]
        let spammer = Tg.user(firstName = "kill retry-429")
        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = spammer)
        let! _ = fixture.SendMessage msg

        // Retry happened and produced a real verdict.
        let! calls = fixture.GetAzureLlmCalls()
        Assert.True(calls.Length >= 2, $"expected a retry (>=2 Azure calls), got {calls.Length}")
        let! verdict = fixture.TryGetLlmTriageVerdict msg.Message.Value
        Assert.Equal(Some "SPAM", verdict)
        let! deleted = fixture.MessageIsAutoDeleted msg.Message.Value
        Assert.True(deleted, "after retry the spam is deleted, not leaked")

        // A successful (retried) SPAM goes to the detected-spam channel, never the action channel.
        let! sends = fixture.GetFakeCalls "sendMessage"
        Assert.Empty(leaksToActionChannel sends)
    }

    [<Fact>]
    let ``LLM triage exhausts retries on a persistent 429 and surfaces the message to humans`` () = task {
        // 4 scripted 429s = the initial attempt + 3 retries (MaxRetryAttempts=3), so every attempt
        // fails and none are left queued. On master only ONE call is made (no retry).
        do! resetFakes ()
        do! fixture.SetAzureLlmScript [| http429; http429; http429; http429 |]
        let spammer = Tg.user(firstName = "kill persistent-429")
        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = spammer)
        let! _ = fixture.SendMessage msg

        // Retries were attempted (master would give up after the first call).
        let! calls = fixture.GetAzureLlmCalls()
        Assert.True(calls.Length >= 2, $"expected retries (>=2 Azure calls), got {calls.Length}")

        // Persistent failure is fail-safe: not auto-deleted, surfaced to the vahter action channel.
        let! deleted = fixture.MessageIsAutoDeleted msg.Message.Value
        Assert.False(deleted, "must NOT auto-delete when the LLM never succeeded")
        let! sends = fixture.GetFakeCalls "sendMessage"
        Assert.NotEmpty(leaksToActionChannel sends)
    }

    interface IClassFixture<MlAwaitFixture>
