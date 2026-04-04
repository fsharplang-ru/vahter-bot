module VahterBanBot.Tests.MLScoreDeterminismTests

open Xunit
open VahterBanBot.Tests.ContainerTestBase
open VahterBanBot.Tests.TgMessageUtils

/// Asserts exact ML scores for known texts to catch non-determinism in the training pipeline.
/// If this test fails, it means something changed the ML model output — likely:
///   - test_seed.sql was modified (different training data)
///   - ML training parameters changed
///   - training data ordering became non-deterministic again
///
/// Scores are raw SDCA logistic regression values (unbounded, not calibrated [0,1]).
/// ML_SPAM_THRESHOLD = 1.0 (score >= 1.0 → detected spam, auto-deleted)
/// ML_WARNING_THRESHOLD = 0.0 (score >= 0.0 and < 1.0 → potential spam, LLM triage)
/// Score < 0.0 → ham, ignored by ML pipeline.
///
/// Expected values were captured from a deterministic run with ML_SEED=42
/// and staggered seed timestamps (each row +1 second).
type MLScoreDeterminismTests(fixture: MlEnabledVahterTestContainers, _ml: MlAwaitFixture) =

    [<Theory>]
    // -- Potential spam (0.0 ≤ score < 1.0): triggers LLM triage + callbacks.
    // These are the most fragile scores — small model changes flip test behavior.
    [<InlineData("77",  0.023787736892700195)>]   // key boundary text: LlmTriageTests + MLBanTests depend on this being potential spam
    [<InlineData("33",  0.12214356660842896)>]    // used in MLBanTests for potential spam callbacks

    // -- Detected spam (score ≥ 1.0): auto-deleted, NotASpam callback only.
    [<InlineData("2222222",  1.5686224699020386)>]  // primary detected spam text in MLBanTests
    [<InlineData("7777777",  1.6370898485183716)>]  // detected spam, used for false-positive button tests
    [<InlineData("3333333",  1.6686757802963257)>]  // detected spam, false-negative scenario
    [<InlineData("66666666", 1.7572427988052368)>]  // detected spam, used in multiple MLBanTests

    // -- Texts scoring in unexpected ranges (but deterministic).
    [<InlineData("a",     -1.0202232599258423)>]  // ham text with strong negative score, used in MLBanTests to verify ham is ignored by ML pipeline
    [<InlineData("hello", -0.19999688863754272)>] // ham text with borderline score, used in MLBanTests to verify ham is ignored by ML pipeline and doesn't trigger triage due to short length

    // -- Ham (score < 0.0): ignored by ML pipeline, no action taken.
    [<InlineData("b",     -0.63716220855712891)>]   // single letter ham, used in MLBanTests
    [<InlineData("1",     -0.19999688863754272)>]  // single digit from seed — short length keeps score below threshold
    let ``ML score is deterministic for known texts`` (text: string, expectedScore: float) = task {
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = text)
        let! _ = fixture.SendMessage msgUpdate

        let! score = fixture.GetMlScore msgUpdate.Message

        match score with
        | Some actual ->
            let ctx = TestContext.Current
            ctx.TestOutputHelper.WriteLine($"ML score for \"{text}\": {actual}")
            // Exact match (10 decimal places) — any drift means the model changed.
            Assert.Equal(expectedScore, actual, 10)
        | None ->
            Assert.Fail($"No MlScoredMessage event found for \"{text}\"")
    }

    interface IClassFixture<MlAwaitFixture>
