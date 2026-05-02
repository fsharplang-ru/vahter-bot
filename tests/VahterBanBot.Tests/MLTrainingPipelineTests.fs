module VahterBanBot.Tests.MLTrainingPipelineTests

open Xunit
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra

/// Smoke test for the ML training pipeline.
///
/// MLScoreDeterminismTests and MLBanTests run against a pre-loaded fixture model
/// (tests/VahterBanBot.Tests/ml-model.bin) so they don't depend on training.
/// This class uses a separate fixture (MlTrainingFromScratchTestContainers) that
/// deliberately starts with no model in the DB — forcing the bot's prod
/// MachineLearning.StartAsync to train end-to-end.
///
/// The training pipeline is the most important property of this bot (it's what
/// kills 90% of spam autonomously), so we assert it can:
///   1. Train successfully from seed data within a reasonable timeout
///   2. Produce a model that scores known-spam higher than known-ham (relative ordering)
///
/// We do NOT assert exact scores here — those vary across CPU architectures.
/// That's the entire reason the rest of the suite uses a pinned model binary.
type MLTrainingPipelineTests(fixture: MlTrainingFromScratchTestContainers) =

    [<Fact>]
    let ``Production training pipeline produces a model that ranks spam above ham`` () = task {
        // Fixture's AfterStart already polled /ready, so the model trained successfully.
        // Now verify it produces semantically reasonable scores: known-spam > known-ham.
        let spamMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222")
        let hamMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "b")

        let! _ = fixture.SendMessage spamMsg
        let! _ = fixture.SendMessage hamMsg

        let! spamScore = fixture.GetMlScore spamMsg.Message
        let! hamScore = fixture.GetMlScore hamMsg.Message

        Assert.True(spamScore.IsSome, "spam message should have an ML score recorded")
        Assert.True(hamScore.IsSome, "ham message should have an ML score recorded")
        Assert.True(
            spamScore.Value > hamScore.Value,
            $"Trained model should rank spam above ham. spam={spamScore.Value}, ham={hamScore.Value}"
        )
    }
