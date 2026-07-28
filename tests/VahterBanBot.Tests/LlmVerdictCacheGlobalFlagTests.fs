module VahterBanBot.Tests.LlmVerdictCacheGlobalFlagTests

open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

/// Exercises the LLM_VERDICT_CACHE_GLOBAL_ENABLED=false escape hatch (env-only — see
/// Program.fs's buildBotConf doc comment) against a SEPARATE container
/// (LlmVerdictCacheGlobalDisabledTestContainers), since the flag is an app env var fixed at
/// container startup, not a hot-reloadable bot_setting.
///
/// With the flag off, the verdict cache reverts fully to its pre-PR behavior: every verdict,
/// including SPAM/SKIP, is scoped per-sender only — two different senders with identical text
/// are NEVER deduped. This is exactly the property LlmTriageResilienceTests narrows to NOT_SPAM
/// only when the flag is on (default).
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
        // The per-sender tier itself must keep working with the flag off — this is a sanity check
        // that "flag off" means "revert to legacy scoping", not "cache disabled entirely".
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
    }

    interface IClassFixture<MlAwaitFixture>
