module VahterBanBot.Tests.LlmTriageTests

open VahterBanBot.Tests.ContainerTestBase
open VahterBanBot.Tests.TgMessageUtils
open Xunit

/// LLM triage tests share the ML-enabled container (which also has LLM_TRIAGE_ENABLED=true).
/// Test names start with "LLM triage" so they sort after "I" (inline keyboard, if-message tests)
/// but before "M"/"P" tests that insert "77" into false_positive_messages — avoiding DB contamination.
///
/// "77" scores in the ML warning range (>= ML_WARNING_THRESHOLD=0.0, < ML_SPAM_THRESHOLD=1.0).
/// The fake Azure OpenAI handler uses three-way routing on the user message content (role="user" only):
///   firstName containing "kill"  → SPAM     (delete + reduce karma)
///   firstName containing "spam"  → SKIP     (human triage)
///   neither                      → NOT_SPAM
type LlmTriageTests(fixture: MlEnabledVahterTestContainers, _ml: MlAwaitFixture) =

    [<Fact>]
    let ``LLM triage SPAM verdict deletes message and reduces karma without banning`` () = task {
        // Display name contains "kill" → fake LLM handler returns SPAM → message deleted, karma reduced, no instant ban
        let spammer = Tg.user(firstName = "kill advertiser")
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = spammer)
        let! _ = fixture.SendMessage msgUpdate

        // LLM is now synchronous — verdict committed before handler returns
        let! verdict = fixture.TryGetLlmTriageVerdict msgUpdate.Message
        Assert.Equal(Some "SPAM", verdict)

        // User must NOT be instantly banned — goes through karma system instead
        let! isBannedByAI = fixture.UserBannedByAI spammer.Id
        Assert.False(isBannedByAI, "User should NOT be instantly banned after SPAM verdict — karma system handles it")

        // Message should be auto-deleted (deleteSpam records BotAutoDeleted)
        let! wasAutoDeleted = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.True(wasAutoDeleted, "Message should be auto-deleted after SPAM verdict")
    }

    [<Fact>]
    let ``LLM triage SKIP verdict routes to human triage without banning`` () = task {
        // Display name contains "spam" (but not "kill") → fake LLM handler returns SKIP → human triage
        let spammer = Tg.user(firstName = "spam advertiser")
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = spammer)
        let! _ = fixture.SendMessage msgUpdate

        let! verdict = fixture.TryGetLlmTriageVerdict msgUpdate.Message
        Assert.Equal(Some "SKIP", verdict)

        // User must NOT be banned — SKIP goes to human triage
        let! isBannedByAI = fixture.UserBannedByAI spammer.Id
        Assert.False(isBannedByAI, "User should NOT be banned for SKIP verdict — goes to humans")

        let! isBannedByVahter = fixture.UserBanned spammer.Id
        Assert.False(isBannedByVahter, "User should NOT be banned by vahter either")
    }

    [<Fact>]
    let ``LLM triage NOT_SPAM verdict takes no action`` () = task {
        // Default user firstName is a Guid — no keywords → NOT_SPAM
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77")
        let! _ = fixture.SendMessage msgUpdate

        let! verdict = fixture.TryGetLlmTriageVerdict msgUpdate.Message
        Assert.Equal(Some "NOT_SPAM", verdict)

        // User must NOT be banned
        let! isBannedByAI = fixture.UserBannedByAI msgUpdate.Message.From.Id
        Assert.False(isBannedByAI, "User should NOT be banned for NOT_SPAM verdict")

        // No callback should be posted to triage channel
        let! hasTriggerCallback = fixture.MessageBanned msgUpdate.Message
        Assert.False(hasTriggerCallback, "Message should not be flagged for NOT_SPAM verdict")
    }

    [<Fact>]
    let ``LLM triage does not fire for detected spam`` () = task {
        // "2222222" scores >= ML_SPAM_THRESHOLD (1.0) → detected spam path → no LLM call
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222")
        let! _ = fixture.SendMessage msgUpdate

        let! verdict = fixture.TryGetLlmTriageVerdict msgUpdate.Message
        Assert.Equal(None, verdict)
    }

    [<Fact>]
    let ``LLM triage SPAM verdict stores message in DB before deleting`` () = task {
        // Regression: DB.insertMessage was called after processMessage, so deleteSpam → getUserMessages
        // found 0 messages even though the spam message had just been received.
        let spammer = Tg.user(firstName = "kill message-count regression")
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = spammer)
        let! _ = fixture.SendMessage msgUpdate

        // Message must be stored in the event store (inserted before deletion ran, not after)
        let! dbMsg = fixture.TryGetDbMessage msgUpdate.Message
        Assert.True(dbMsg.IsSome, "Message should be stored in DB even after LLM SPAM verdict")

        // Message must have a BotAutoDeleted event (deleteSpam path records it)
        let! wasAutoDeleted = fixture.MessageIsAutoDeleted msgUpdate.Message
        Assert.True(wasAutoDeleted, "Message should have BotAutoDeleted event after SPAM verdict")
    }

    [<Fact>]
    let ``LLM triage SPAM event contains modelName and promptHash`` () = task {
        let spammer = Tg.user(firstName = "kill spammer with metadata")
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "77", from = spammer)
        let! _ = fixture.SendMessage msgUpdate

        let! modelName = fixture.TryGetLlmClassifiedModelName msgUpdate.Message
        Assert.True(modelName.IsSome, "LlmClassified event should contain modelName")
        Assert.False(System.String.IsNullOrEmpty(modelName.Value), "modelName should not be empty")

        let! promptHash = fixture.TryGetLlmClassifiedPromptHash msgUpdate.Message
        Assert.True(promptHash.IsSome, "LlmClassified event should contain promptHash")
        Assert.False(System.String.IsNullOrEmpty(promptHash.Value), "promptHash should not be empty")
    }

    interface IClassFixture<MlAwaitFixture>
