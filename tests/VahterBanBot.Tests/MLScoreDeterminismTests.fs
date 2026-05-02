module VahterBanBot.Tests.MLScoreDeterminismTests

open Xunit
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra

/// Asserts ML scores for known texts against the pre-trained fixture model.
///
/// The bot loads `tests/VahterBanBot.Tests/ml-model.bin` instead of training from scratch
/// (see ContainerTestBase.SeedDatabase). This eliminates SDCA's cross-architecture training
/// nondeterminism — scoring against a fixed model is a single sparse dot product whose
/// FP noise is bounded by a few ULPs even when SIMD reduction order differs between
/// Windows/x86_64 and Linux/ARM64.
///
/// If this test fails, the fixture model and code disagree. Causes:
///   - ml-model.bin was regenerated but expected values weren't updated
///   - test_seed.sql changed in a way that changes the model semantics
///   - someone trained on a different ML_SEED / training params and re-committed the binary
///
/// Scores are raw SDCA logistic regression values (unbounded, not calibrated [0,1]).
/// ML_SPAM_THRESHOLD = 1.0 (score >= 1.0 → detected spam, auto-deleted)
/// ML_WARNING_THRESHOLD = 0.0 (score >= 0.0 and < 1.0 → potential spam, LLM triage)
/// Score < 0.0 → ham, ignored by ML pipeline.
///
/// Tolerance: 4 decimal places (~1e-4). Tight enough to catch a model swap; loose enough
/// to absorb cross-arch FP noise in the prediction dot product.
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
            // 4 decimal places: catches model swap, tolerates cross-arch FP noise in dot product.
            Assert.Equal(expectedScore, actual, 4)
        | None ->
            Assert.Fail($"No MlScoredMessage event found for \"{text}\"")
    }

    interface IClassFixture<MlAwaitFixture>
