// ML Experiment: Baseline vs Time-Decay Weighted model comparison
// Usage: open in F# Interactive (dotnet fsi ml-experiment.fsx)
// or evaluate blocks interactively in your editor / REPL
//
// Set your connection string below before running.

#r "nuget: Microsoft.ML, 5.0.0"
#r "nuget: Npgsql, 10.0.1"
#r "nuget: Dapper, 2.1.66"

open System
open System.Collections.Generic
open Microsoft.ML
open Microsoft.ML.Data
open Npgsql
open Dapper

// ============================================================================
// Configuration - EDIT THESE
// ============================================================================

let connectionString = "SECRET"
let criticalMsgCount = 5       // same as ML_TRAIN_CRITICAL_MSG_COUNT
let customEmojiThreshold = 20  // same as ML_CUSTOM_EMOJI_THRESHOLD
let maxIterations = 50         // same as ML_MAX_NUMBER_OF_ITERATIONS
let testFraction = 0.2         // same as ML_TRAINING_SET_FRACTION
let trainWindowDays = 60      // training window in days
let shuffleData = true         // same as ML_TRAIN_RANDOM_SORT_DATA
let mlSeed = Nullable<int>()   // same as ML_SEED (null = random)
let spamThreshold = 1.0f       // same as ML_SPAM_THRESHOLD

// Time-decay parameter: w(t) = exp(-k * age_in_days)
let k = 0.01

// ============================================================================
// Types (from DB.fs and ML.fs)
// ============================================================================

[<CLIMutable>]
type SpamOrHamDb =
    { text: string
      spam: bool
      less_than_n_messages: bool
      custom_emoji_count: int
      created_at: DateTime }

[<CLIMutable>]
type SpamOrHam =
    { text: string
      spam: bool
      lessThanNMessagesF: single
      moreThanNEmojisF: single
      weight: single
      createdAt: DateTime }

[<CLIMutable>]
type Prediction =
    { [<ColumnName("PredictedLabel")>]
      PredictedLabel: bool
      Score: single
      Probability: single }

// ============================================================================
// Data loading (SQL from DB.fs mlData)
// ============================================================================

let loadData () =
    let criticalDate = DateTime.UtcNow - TimeSpan.FromDays(float trainWindowDays)
    use conn = new NpgsqlConnection(connectionString)
    conn.Open()

    let sql =
        """
WITH custom_emojis AS (SELECT message.id, COUNT(*) FILTER (WHERE entities ->> 'type' = 'custom_emoji') AS custom_emoji_count
                       FROM message,
                            LATERAL JSONB_ARRAY_ELEMENTS(raw_message -> 'entities') AS entities
                       GROUP BY message.id),
     less_than_n_messages AS (SELECT u.id, COUNT(DISTINCT m.text) < @criticalMsgCount AS less_than_n_messages
                              FROM "user" u
                                       LEFT JOIN message m ON u.id = m.user_id
                              GROUP BY u.id),
     really_banned AS (SELECT *
                       FROM banned b
                       -- known false positive spam messages
                       WHERE NOT EXISTS(SELECT 1 FROM false_positive_users fpu WHERE fpu.user_id = b.banned_user_id)
                         AND NOT EXISTS(SELECT 1
                                        FROM false_positive_messages fpm
                                        WHERE fpm.text_hash = md5(b.message_text)::uuid
                                          AND fpm.text = b.message_text)
                         AND b.message_text IS NOT NULL
                         AND b.banned_at >= @criticalDate),
     spam_or_ham AS (SELECT x.text,
                            x.spam,
                            x.less_than_n_messages,
                            x.custom_emoji_count,
                            MAX(x.created_at) AS created_at
                     FROM (SELECT DISTINCT COALESCE(m.text, re_id.message_text)                       AS text,
                                           CASE
                                               -- known false negative spam messages
                                               WHEN (EXISTS(SELECT 1
                                                            FROM false_negative_messages fnm
                                                            WHERE fnm.chat_id = m.chat_id
                                                              AND fnm.message_id = m.message_id)
                                                   -- known banned spam messages by bot, and not marked as false positive
                                                   OR EXISTS(SELECT 1
                                                             FROM banned_by_bot bbb
                                                             WHERE bbb.banned_in_chat_id = m.chat_id
                                                               AND bbb.message_id = m.message_id))
                                                   THEN TRUE
                                               WHEN re_id.banned_user_id IS NULL AND re_text.banned_user_id IS NULL
                                                   THEN FALSE
                                               ELSE TRUE
                                               END                                                    AS spam,
                                           COALESCE(l.less_than_n_messages, TRUE)                     AS less_than_n_messages,
                                           COALESCE(ce.custom_emoji_count, 0)                         AS custom_emoji_count,
                                           COALESCE(re_id.banned_at, re_text.banned_at, m.created_at) AS created_at
                           FROM (SELECT *
                                 FROM message
                                 WHERE text IS NOT NULL
                                   AND created_at >= @criticalDate) m
                                    FULL OUTER JOIN really_banned re_id
                                                    ON m.message_id = re_id.message_id AND m.chat_id = re_id.banned_in_chat_id
                                    LEFT JOIN really_banned re_text ON m.text = re_text.message_text
                                    LEFT JOIN custom_emojis ce ON m.id = ce.id
                                    LEFT JOIN less_than_n_messages l ON m.user_id = l.id) x
                     GROUP BY text, spam, less_than_n_messages, custom_emoji_count)
SELECT *
FROM spam_or_ham
ORDER BY created_at;
"""

    let data = conn.Query<SpamOrHamDb>(sql, {| criticalDate = criticalDate; criticalMsgCount = criticalMsgCount |})
    Array.ofSeq data

// ============================================================================
// Model training
// ============================================================================

type TrainResult =
    { Model: ITransformer
      Metrics: CalibratedBinaryClassificationMetrics
      Engine: PredictionEngine<SpamOrHam, Prediction>
      TrainData: SpamOrHam array
      TestData: SpamOrHam array }

/// Splits data into train/test sets (deterministic given the same input order).
/// Both models must use the same split for a fair comparison.
let splitData (data: SpamOrHam array) =
    let mlContext = MLContext(mlSeed)
    let shuffled =
        if shuffleData then
            let copy = Array.copy data
            Array.sortInPlaceBy (fun _ -> Guid.NewGuid()) copy
            copy
        else
            data

    let dataView = mlContext.Data.LoadFromEnumerable shuffled
    let split = mlContext.Data.TrainTestSplit(dataView, testFraction = testFraction)

    let trainItems =
        mlContext.Data.CreateEnumerable<SpamOrHam>(split.TrainSet, reuseRowObject = false)
        |> Array.ofSeq
    let testItems =
        mlContext.Data.CreateEnumerable<SpamOrHam>(split.TestSet, reuseRowObject = false)
        |> Array.ofSeq

    trainItems, testItems

/// Trains a model on pre-split data.
/// When useWeighting=true, the 'weight' field is used via exampleWeightColumnName.
/// When useWeighting=false, 'weight' field is ignored (standard training).
let trainModel (trainData: SpamOrHam array) (testData: SpamOrHam array) (useWeighting: bool) =
    let mlContext = MLContext(mlSeed)

    let trainView = mlContext.Data.LoadFromEnumerable trainData
    let testView = mlContext.Data.LoadFromEnumerable testData

    let textPipeline =
        mlContext.Transforms.Text
            .FeaturizeText(outputColumnName = "TextFeaturized", inputColumnName = "text")
            .Append(
                mlContext.Transforms.Concatenate(
                    outputColumnName = "Features",
                    inputColumnNames = [| "TextFeaturized"; "lessThanNMessagesF"; "moreThanNEmojisF" |]
                )
            )

    let pipeline =
        if useWeighting then
            textPipeline.Append(
                mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(
                    labelColumnName = "spam",
                    featureColumnName = "Features",
                    exampleWeightColumnName = "weight",
                    maximumNumberOfIterations = Nullable maxIterations
                )
            )
        else
            textPipeline.Append(
                mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(
                    labelColumnName = "spam",
                    featureColumnName = "Features",
                    maximumNumberOfIterations = Nullable maxIterations
                )
            )

    let trainedModel = pipeline.Fit(trainView)
    let predictions = trainedModel.Transform(testView)
    let metrics =
        mlContext.BinaryClassification.Evaluate(
            data = predictions,
            labelColumnName = "spam",
            scoreColumnName = "Score"
        )

    let engine = mlContext.Model.CreatePredictionEngine<SpamOrHam, Prediction>(trainedModel)

    { Model = trainedModel
      Metrics = metrics
      Engine = engine
      TrainData = trainData
      TestData = testData }

// ============================================================================
// Metrics comparison
// ============================================================================

let printMetricsComparison (baseline: CalibratedBinaryClassificationMetrics) (weighted: CalibratedBinaryClassificationMetrics) =
    let header = sprintf "%-35s %12s %12s" "Metric" "Baseline" "Weighted"
    let sep = String('-', header.Length)
    printfn ""
    printfn "%s" header
    printfn "%s" sep
    let row name (b: float) (w: float) =
        let diff = w - b
        let arrow = if diff > 0.001 then " (+)" elif diff < -0.001 then " (-)" else ""
        printfn "%-35s %12.6f %12.6f%s" name b w arrow
    row "Accuracy" baseline.Accuracy weighted.Accuracy
    row "F1Score" baseline.F1Score weighted.F1Score
    row "PositivePrecision (Spam)" baseline.PositivePrecision weighted.PositivePrecision
    row "PositiveRecall (Spam)" baseline.PositiveRecall weighted.PositiveRecall
    row "NegativePrecision (Ham)" baseline.NegativePrecision weighted.NegativePrecision
    row "NegativeRecall (Ham)" baseline.NegativeRecall weighted.NegativeRecall
    row "AreaUnderPrecisionRecallCurve" baseline.AreaUnderPrecisionRecallCurve weighted.AreaUnderPrecisionRecallCurve
    row "LogLoss" baseline.LogLoss weighted.LogLoss
    row "Entropy" baseline.Entropy weighted.Entropy
    printfn "%s" sep
    printfn ""
    printfn "Baseline confusion matrix:"
    printfn "%s" (baseline.ConfusionMatrix.GetFormattedConfusionTable())
    printfn "Weighted confusion matrix:"
    printfn "%s" (weighted.ConfusionMatrix.GetFormattedConfusionTable())

// ============================================================================
// Per-interval precision/recall evaluation
// ============================================================================

type IntervalStats =
    { IntervalName: string
      Total: int
      SpamCount: int
      HamCount: int
      TP: int; FP: int; FN: int; TN: int
      Precision: float
      Recall: float
      F1: float }

let evaluateByDateInterval
    (baselineEngine: PredictionEngine<SpamOrHam, Prediction>)
    (weightedEngine: PredictionEngine<SpamOrHam, Prediction>)
    (testData: SpamOrHam array)
    (threshold: single)
    =
    let now = DateTime.UtcNow

    let intervals =
        [| ("0-7 days",    0.0,   7.0)
           ("7-30 days",   7.0,  30.0)
           ("30-60 days", 30.0,  60.0)
           ("60-90 days", 60.0,  90.0)
           ("90-180 days",90.0, 180.0)
           ("180+ days", 180.0, 99999.0) |]

    let calcStats (engine: PredictionEngine<SpamOrHam, Prediction>) (items: SpamOrHam array) intervalName =
        let mutable tp, fp, fn, tn = 0, 0, 0, 0
        let spamCount = items |> Array.filter (fun x -> x.spam) |> Array.length

        for item in items do
            let pred = engine.Predict(item)
            let predictedSpam = pred.Score >= threshold
            match item.spam, predictedSpam with
            | true,  true  -> tp <- tp + 1
            | false, true  -> fp <- fp + 1
            | true,  false -> fn <- fn + 1
            | false, false -> tn <- tn + 1

        let precision = if tp + fp > 0 then float tp / float (tp + fp) else 0.0
        let recall = if tp + fn > 0 then float tp / float (tp + fn) else 0.0
        let f1 = if precision + recall > 0.0 then 2.0 * precision * recall / (precision + recall) else 0.0

        { IntervalName = intervalName
          Total = items.Length
          SpamCount = spamCount
          HamCount = items.Length - spamCount
          TP = tp; FP = fp; FN = fn; TN = tn
          Precision = precision
          Recall = recall
          F1 = f1 }

    printfn ""
    printfn "=== Per-Interval Precision/Recall (threshold = %.2f) ===" threshold
    printfn ""

    let header =
        sprintf "%-14s | %5s %5s %5s | %-9s  %8s %8s %8s | %-9s  %8s %8s %8s"
            "Interval" "Total" "Spam" "Ham"
            "BASELINE" "Prec" "Recall" "F1"
            "WEIGHTED" "Prec" "Recall" "F1"
    printfn "%s" header
    printfn "%s" (String('-', header.Length))

    for (name, minDays, maxDays) in intervals do
        let items =
            testData
            |> Array.filter (fun x ->
                let ageDays = (now - x.createdAt).TotalDays
                ageDays >= minDays && ageDays < maxDays)

        if items.Length > 0 then
            let bStats = calcStats baselineEngine items name
            let wStats = calcStats weightedEngine items name

            printfn "%-14s | %5d %5d %5d | %-9s  %8.4f %8.4f %8.4f | %-9s  %8.4f %8.4f %8.4f"
                name
                bStats.Total bStats.SpamCount bStats.HamCount
                "" bStats.Precision bStats.Recall bStats.F1
                "" wStats.Precision wStats.Recall wStats.F1
        else
            printfn "%-14s | %5d %5s %5s | %-9s  %8s %8s %8s | %-9s  %8s %8s %8s"
                name 0 "-" "-" "" "-" "-" "-" "" "-" "-" "-"

    printfn ""

// ============================================================================
// Run the experiment
// ============================================================================

printfn "Loading data from DB..."
let rawData = loadData ()

printfn "Loaded %d samples" rawData.Length

let spamCount = rawData |> Array.filter (fun x -> x.spam) |> Array.length
let hamCount = rawData.Length - spamCount
printfn "  Spam: %d (%.2f%%)" spamCount (100.0 * float spamCount / float rawData.Length)
printfn "  Ham:  %d (%.2f%%)" hamCount (100.0 * float hamCount / float rawData.Length)

let now = DateTime.UtcNow

// Map to ML types - baseline (all weights = 1)
let baselineData =
    rawData
    |> Array.map (fun x ->
        { text = x.text
          spam = x.spam
          createdAt = x.created_at
          moreThanNEmojisF = if x.custom_emoji_count > customEmojiThreshold then 1.0f else 0.0f
          lessThanNMessagesF = if x.less_than_n_messages then 1.0f else 0.0f
          weight = 1.0f })

// Map to ML types - weighted (time-decay)
let weightedData =
    rawData
    |> Array.map (fun x ->
        let ageDays = (now - x.created_at).TotalDays
        let w = single (Math.Exp(-k * ageDays))
        { text = x.text
          spam = x.spam
          createdAt = x.created_at
          moreThanNEmojisF = if x.custom_emoji_count > customEmojiThreshold then 1.0f else 0.0f
          lessThanNMessagesF = if x.less_than_n_messages then 1.0f else 0.0f
          weight = w })

// Show weight distribution
printfn ""
printfn "Weight distribution (k = %.4f):" k
for (name, minD, maxD) in [("0-7d", 0.0, 7.0); ("7-30d", 7.0, 30.0); ("30-60d", 30.0, 60.0); ("60-90d", 60.0, 90.0); ("90-180d", 90.0, 180.0)] do
    let items =
        weightedData
        |> Array.filter (fun x ->
            let age = (now - x.createdAt).TotalDays
            age >= minD && age < maxD)
    if items.Length > 0 then
        let avgW = items |> Array.averageBy (fun x -> float x.weight)
        let minW = items |> Array.minBy (fun x -> x.weight) |> fun x -> x.weight
        let maxW = items |> Array.maxBy (fun x -> x.weight) |> fun x -> x.weight
        let spamN = items |> Array.filter (fun x -> x.spam) |> Array.length
        printfn "  %-10s  count=%5d  spam=%4d  weight: avg=%.4f  min=%.4f  max=%.4f" name items.Length spamN avgW (float minW) (float maxW)

// Split once - both models use the exact same train/test split for fair comparison
printfn ""
printfn "Splitting data (test fraction = %.2f)..." testFraction
let baselineTrain, baselineTest = splitData baselineData

// Build matching weighted versions of the same split
// (same items in same order, but with time-decay weights instead of 1.0)
let toWeightedMap =
    weightedData |> Array.map (fun x -> (x.text, x.spam, x.createdAt), x.weight) |> dict
let applyWeight (item: SpamOrHam) =
    let key = (item.text, item.spam, item.createdAt)
    match toWeightedMap.TryGetValue(key) with
    | true, w -> { item with weight = w }
    | _ -> { item with weight = single (Math.Exp(-k * (now - item.createdAt).TotalDays)) }
let weightedTrain = baselineTrain |> Array.map applyWeight
let weightedTest = baselineTest |> Array.map applyWeight

let trainSpam = baselineTrain |> Array.filter (fun x -> x.spam) |> Array.length
let testSpam = baselineTest |> Array.filter (fun x -> x.spam) |> Array.length
printfn "  Train: %d samples (%d spam)" baselineTrain.Length trainSpam
printfn "  Test:  %d samples (%d spam)" baselineTest.Length testSpam

printfn ""
printfn "Training baseline model (no weighting)..."
let baselineResult = trainModel baselineTrain baselineTest false
printfn "Training weighted model (k = %.4f)..." k
let weightedResult = trainModel weightedTrain weightedTest true

// Overall metrics comparison
printMetricsComparison baselineResult.Metrics weightedResult.Metrics

// Per-interval evaluation on the same test set
evaluateByDateInterval baselineResult.Engine weightedResult.Engine baselineTest spamThreshold

printfn "Done! You can now experiment in the REPL:"
printfn "  - Change 'k' and re-run training"
printfn "  - Call evaluateByDateInterval with different thresholds"
printfn "  - Inspect individual predictions with baselineResult.Engine.Predict(...)"
