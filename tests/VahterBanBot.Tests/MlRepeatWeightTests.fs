module VahterBanBot.Tests.MlRepeatWeightTests

open System
open Microsoft.ML
open VahterBanBot.ML
open VahterBanBot.Tests.ContainerTestBase
open Xunit

// ---------------------------------------------------------------------------
// combineWeight (pure, no DB/container) — proves the flag/decay composition semantics
// described in ML.fs's trainAndSaveModel:
//   repeatWeight = repeatCount (already capped by MlData's SQL) when enabled, else 1.0
//   decayWeight  = exp(-k * ageDays) when k > 0, else 1.0
//   weight       = repeatWeight * decayWeight
// ---------------------------------------------------------------------------

[<Fact>]
let ``combineWeight: repeat flag off collapses a repeated-text weight to 1.0 when decay is also off`` () =
    // 7 repeated messages of the same text (already capped upstream by MlData) would carry a
    // repeat count of 7.0, but the flag is off here so it must NOT reach the trainer.
    Assert.Equal(1.0f, combineWeight false 7.0f 0.0 30.0)

[<Fact>]
let ``combineWeight: repeat flag on passes the already-capped repeat count through unchanged when decay is off`` () =
    Assert.Equal(100.0f, combineWeight true 100.0f 0.0 30.0)

[<Fact>]
let ``combineWeight: decay still applies on its own when the repeat flag is off`` () =
    let k = 0.05
    let ageDays = 10.0
    let expected = single (Math.Exp(-k * ageDays))
    Assert.Equal(float expected, float (combineWeight false 7.0f k ageDays), 6)

[<Fact>]
let ``combineWeight: repeat and decay compose multiplicatively when both are on`` () =
    let k = 0.05
    let ageDays = 10.0
    let repeatCount = 42.0f
    let expected = repeatCount * single (Math.Exp(-k * ageDays))
    Assert.Equal(float expected, float (combineWeight true repeatCount k ageDays), 4)

[<Fact>]
let ``combineWeight: both mechanisms off is a pure no-op (weight always 1.0)`` () =
    Assert.Equal(1.0f, combineWeight false 1.0f 0.0 365.0)
    Assert.Equal(1.0f, combineWeight false 999.0f 0.0 0.0)

// ---------------------------------------------------------------------------
// "Weight reaches the trainer" — an empirical probe, not a reading-the-code argument.
//
// Trains two models via the SAME production pipeline (buildTrainingPipeline, i.e. the exact
// SdcaLogisticRegression + ExampleWeightColumnName="weight" configuration trainAndSaveModel
// uses) on the IDENTICAL rows, differing ONLY in the weight assigned to the spam-labeled rows
// (0.01 vs 100). It then scores a FIXED probe set neither model trained on. If
// ExampleWeightColumnName were silently ignored (e.g. a typo'd column name, or the option never
// set), both models would be trained identically and score the probes identically — this test
// would then fail on the "materially different" assertion below.
// ---------------------------------------------------------------------------

let private spamTexts =
    [ "buy cheap viagra now"; "special discount crypto offer"; "click link to win a prize"
      "free money apply now today"; "cheap replica watches sale"; "limited time crypto bonus offer" ]

let private hamTexts =
    [ "hey how is your day going"; "let's meet for coffee tomorrow"; "great weather today isn't it"
      "thanks so much for the help earlier"; "see you at the meeting later"; "did you watch the game last night" ]

let private mkRow (text: string) (spam: bool) (weight: single) : SpamOrHam =
    { text = text; spam = spam; lessThanNMessagesF = 0.0f; moreThanNEmojisF = 0.0f
      weight = weight; createdAt = DateTime.UtcNow }

/// Trains via the exact production pipeline, varying only spamWeight.
let private trainWith (spamWeight: single) =
    let seed = Nullable 42
    let mlContext = MLContext(seed)
    let data =
        [ for t in spamTexts -> mkRow t true spamWeight
          for t in hamTexts -> mkRow t false 1.0f ]
    let dataView = mlContext.Data.LoadFromEnumerable data
    let pipeline = buildTrainingPipeline mlContext 50 seed true
    let model = pipeline.Fit dataView
    mlContext.Model.CreatePredictionEngine<SpamOrHam, Prediction> model

[<Fact>]
let ``Weight reaches the trainer: SDCA scores shift materially between low- and high-weight spam variants`` () =
    let engineLowWeight = trainWith 0.01f
    let engineHighWeight = trainWith 100.0f

    // Probe texts share vocabulary with the training set but are themselves never trained on.
    let spamProbe = mkRow "buy cheap crypto now" false 1.0f
    let hamProbe = mkRow "let's grab coffee sometime" false 1.0f

    let lowSpamScore = (engineLowWeight.Predict spamProbe).Score
    let highSpamScore = (engineHighWeight.Predict spamProbe).Score
    let ctx = TestContext.Current
    ctx.TestOutputHelper.WriteLine($"spam probe score: low-weight={lowSpamScore}, high-weight={highSpamScore}")

    // A test that would pass even if the weight column were silently ignored is worthless here:
    // require a large, unambiguous separation attributable to the weight alone (same rows,
    // same pipeline, same seed -- the ONLY difference between the two runs is the weight column).
    Assert.True(
        highSpamScore - lowSpamScore > 1.0f,
        $"Expected the high-weight-spam model to score the spam-like probe materially higher \
          than the low-weight-spam model (low={lowSpamScore}, high={highSpamScore})")

    // Sanity check on direction: near-zero spam weight should barely register as spam-like,
    // while the heavily-upweighted spam model should score it clearly positive.
    Assert.True(highSpamScore > 0.0f, $"Expected the high-weight model to score the spam probe positive, got {highSpamScore}")

    let lowHamScore = (engineLowWeight.Predict hamProbe).Score
    let highHamScore = (engineHighWeight.Predict hamProbe).Score
    ctx.TestOutputHelper.WriteLine($"ham probe score: low-weight={lowHamScore}, high-weight={highHamScore}")

    // Only the spam rows were reweighted, so the ham probe's score should move far less than
    // the spam probe's between the two variants.
    Assert.True(
        abs (highHamScore - lowHamScore) < abs (highSpamScore - lowSpamScore),
        $"Ham probe score shift should be smaller than the spam probe's since only spam rows were reweighted \
          (hamShift={abs (highHamScore - lowHamScore)}, spamShift={abs (highSpamScore - lowSpamScore)})")

// ---------------------------------------------------------------------------
// MlData query — repeat-count weighting and cap, against a real Postgres instance.
// Reuses MlDisabledVahterTestContainers (ML disabled, but the event table / MlData query
// itself doesn't depend on ML being enabled) purely for its Postgres + migrated schema; no
// bot behavior beyond direct SQL access via DbService is exercised.
// ---------------------------------------------------------------------------

type MlRepeatWeightQueryTests(fixture: MlDisabledVahterTestContainers) =

    /// Seeds `n` distinct messages sharing one `text`, all authored by `userId` in `chatId`,
    /// each immediately marked spam (MessageMarkedSpam) -- so MlData's GROUP BY on
    /// (text, is_spam, less_than_n_messages, entities) collapses them into exactly one row
    /// whose repeat-count weight is `n` (before the cap).
    let seedRepeatedSpamCampaign (chatId: int64) (userId: int64) (text: string) (n: int) = task {
        use conn = new Npgsql.NpgsqlConnection(fixture.DbConnectionString)
        //language=postgresql
        let seedSql =
            """
INSERT INTO event(stream_id, stream_version, data, created_at)
SELECT 'message:' || @chatId || ':' || s, 1,
       jsonb_build_object('Case', 'MessageReceived', 'chatId', @chatId, 'messageId', s,
                           'userId', @userId, 'text', @text, 'rawMessage', '{}'),
       now()
FROM generate_series(1, @n) AS s
ON CONFLICT DO NOTHING;

INSERT INTO event(stream_id, stream_version, data, created_at)
SELECT 'message:' || @chatId || ':' || s, 2,
       jsonb_build_object('Case', 'MessageMarkedSpam', 'chatId', @chatId, 'messageId', s, 'markedBy', NULL),
       now()
FROM generate_series(1, @n) AS s
ON CONFLICT DO NOTHING;
            """
        let! _ = Dapper.SqlMapper.ExecuteAsync(conn, seedSql, {| chatId = chatId; userId = userId; text = text; n = n |})
        ()
    }

    [<Fact>]
    let ``MlData weights a repeated spam campaign by its repeat count (min(N, cap))`` () = task {
        let chatId = -9001L
        let userId = 900001L
        let text = "repeat weight query test campaign alpha"
        do! seedRepeatedSpamCampaign chatId userId text 7

        let db = VahterBanBot.DbService(fixture.DbConnectionString, TimeProvider.System)
        let! mlData = db.MlData(100, DateTime.UtcNow.AddDays -1.0, 100)

        let row = mlData |> Array.find (fun x -> x.text = text)
        Assert.True(row.spam, "seeded campaign was marked spam")
        Assert.Equal(7.0f, row.weight)
    }

    [<Fact>]
    let ``MlData clamps the repeat weight at the configured cap`` () = task {
        let chatId = -9002L
        let userId = 900002L
        let text = "repeat weight query test campaign beta"
        do! seedRepeatedSpamCampaign chatId userId text 12

        let db = VahterBanBot.DbService(fixture.DbConnectionString, TimeProvider.System)
        // Cap set below the seeded repeat count (12) to prove LEAST(COUNT(*), @repeatWeightCap)
        // actually clamps rather than merely being present in the SELECT list.
        let! mlData = db.MlData(100, DateTime.UtcNow.AddDays -1.0, 5)

        let row = mlData |> Array.find (fun x -> x.text = text)
        Assert.Equal(5.0f, row.weight)
    }

    [<Fact>]
    let ``MlData weight for a never-repeated message is 1`` () = task {
        let chatId = -9003L
        let userId = 900003L
        let text = "repeat weight query test singleton gamma"
        do! seedRepeatedSpamCampaign chatId userId text 1

        let db = VahterBanBot.DbService(fixture.DbConnectionString, TimeProvider.System)
        let! mlData = db.MlData(100, DateTime.UtcNow.AddDays -1.0, 100)

        let row = mlData |> Array.find (fun x -> x.text = text)
        Assert.Equal(1.0f, row.weight)
    }
