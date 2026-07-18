module VahterBanBot.Tests.LlmTriageResilienceTests

open System.Threading.Tasks
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

/// Thorough LLM-triage tests for the verdict cache, concurrent single-flight dedup, and 429
/// retry/backoff. They assert ONLY on observable outcomes — the exact number of Azure
/// chat-completion calls, auto-deletion, the recorded verdict, and whether a message leaked to
/// the vahter action channel — so the same tests are meaningful on the un-fixed master branch,
/// where they go red:
///   • dedup / single-flight tests  → master makes N Azure calls, the fix makes 1.
///   • 429-retry tests              → master makes 1 call then leaks to the action channel;
///                                    the fix retries (>=2 calls) and handles the message.
///
/// Test names start with "LLM triage" to share the ML-enabled container's name-ordering band.
type LlmTriageResilienceTests(fixture: MlEnabledVahterTestContainers, _ml: MlAwaitFixture) =

    // One scripted Azure rate-limit response. The bot does not parse a non-2xx body, so its
    // contents are irrelevant; only the 429 status matters.
    let http429: AzureScriptedResponse =
        { status = 429; body = """{"error":{"code":"429","message":"rate limited"}}"""; delayMs = 0; errorMode = "" }

    // Resets recorded fake calls (Azure + Telegram) and clears any leftover LLM 429 script, so each
    // test starts clean and call-count assertions only observe this test's traffic.
    let resetFakes () = task {
        do! fixture.ClearAzureOcrCalls()   // clears ALL recorded fake-Azure calls (OCR + LLM)
        do! fixture.ClearFakeCalls()       // clears recorded fake-Telegram calls
        do! fixture.SetAzureLlmScript [||] // clears any queued LLM scripted responses
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
    }

    [<Fact>]
    let ``LLM triage does NOT dedup different users posting identical text`` () = task {
        // Correctness guard: the cache key is per-user, so two different spammers with identical
        // text must each be classified. Passes on both branches — protects against over-dedup.
        do! resetFakes ()
        let a = Tg.user(firstName = "kill userA")
        let b = Tg.user(firstName = "kill userB")
        let! _ = fixture.SendMessage (Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = a))
        let! _ = fixture.SendMessage (Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = b))

        let! calls = fixture.GetAzureLlmCalls()
        Assert.Equal(2, calls.Length)
    }

    // ── Concurrency (single-flight) ───────────────────────────────────────────

    [<Fact>]
    let ``LLM triage collapses a concurrent multi-channel burst into ONE Azure call`` () = task {
        // The literal scenario: a spammer posts the same text to several channels at once and the
        // webhooks arrive concurrently. Single-flight must coalesce them onto one Azure call.
        // On master each of the 5 fires its own call.
        do! resetFakes ()
        let spammer = Tg.user(firstName = "kill concurrent-burst")
        let msgs =
            [ for i in 0 .. 4 -> Tg.quickMsg(chat = fixture.ChatsToMonitor[i % 2], text = "77", from = spammer) ]

        // Fire all webhooks concurrently.
        let! _ = Task.WhenAll(msgs |> List.map (fun m -> fixture.SendMessage m))

        let! calls = fixture.GetAzureLlmCalls()
        Assert.Equal(1, calls.Length)

        // Every copy is still handled (deleted) from the shared verdict.
        for m in msgs do
            let! deleted = fixture.MessageIsAutoDeleted m.Message.Value
            Assert.True(deleted, "each concurrent copy should be auto-deleted")
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
