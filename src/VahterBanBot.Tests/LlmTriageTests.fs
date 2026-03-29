module VahterBanBot.Tests.LlmTriageTests

open System.Threading.Tasks
open VahterBanBot.Tests.ContainerTestBase
open VahterBanBot.Tests.TgMessageUtils
open Xunit

/// LLM triage tests share the ML-enabled container (which also has LLM_TRIAGE_ENABLED=true).
/// Test names start with "LLM triage" so they sort after "I" (inline keyboard, if-message tests)
/// but before "M"/"P" tests that insert "77" into false_positive_messages — avoiding DB contamination.
///
/// "77" scores in the ML warning range (>= ML_WARNING_THRESHOLD=0.0, < ML_SPAM_THRESHOLD=1.0).
/// The fake Azure OpenAI handler returns KILL when the user message contains "spam" (case-insensitive,
/// role="user" only), NOT_SPAM otherwise. Setting a user firstName containing "spam" triggers KILL.
type LlmTriageTests(fixture: MlEnabledVahterTestContainers, _ml: MlAwaitFixture) =

    [<Fact>]
    let ``LLM triage fires KILL verdict for potential spam`` () = task {
        // Display name "spam advertiser" → fake LLM handler sees "spam" in user message → KILL
        let spammer = Tg.user(firstName = "spam advertiser")
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = spammer)
        let! _ = fixture.SendMessage msgUpdate

        // Fire-and-forget — give it a moment to complete
        do! Task.Delay 2000

        let! verdict = fixture.TryGetLlmTriageVerdict msgUpdate.Message
        Assert.Equal(Some "KILL", verdict)
    }

    [<Fact>]
    let ``LLM triage fires NOT_SPAM verdict for potential spam`` () = task {
        // Default user firstName is a Guid — no "spam" keyword → NOT_SPAM
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77")
        let! _ = fixture.SendMessage msgUpdate

        do! Task.Delay 2000

        let! verdict = fixture.TryGetLlmTriageVerdict msgUpdate.Message
        Assert.Equal(Some "NOT_SPAM", verdict)
    }

    [<Fact>]
    let ``LLM triage does not fire for detected spam`` () = task {
        // "2222222" scores >= ML_SPAM_THRESHOLD (1.0) → detected spam path → no LLM call
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222")
        let! _ = fixture.SendMessage msgUpdate

        do! Task.Delay 2000

        let! verdict = fixture.TryGetLlmTriageVerdict msgUpdate.Message
        Assert.Equal(None, verdict)
    }

    interface IClassFixture<MlAwaitFixture>
