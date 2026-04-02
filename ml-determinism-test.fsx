// ML.NET Determinism Test
// Run twice with: dotnet fsi ml-determinism-test.fsx
// If ML.NET is deterministic with same seed + same data order, scores must match exactly.

#r "nuget: Microsoft.ML, 5.0.0"

open System
open Microsoft.ML
open Microsoft.ML.Data

[<CLIMutable>]
type SpamOrHam =
    { text: string
      spam: bool
      lessThanNMessagesF: single
      moreThanNEmojisF: single
      weight: single }

[<CLIMutable>]
type Prediction =
    { Score: single
      text: string
      spam: bool }

// Hardcoded training data — a small but varied set
let trainingData = [|
    // Spam examples
    { text = "Buy cheap viagra now"; spam = true; lessThanNMessagesF = 1.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "Free money click here"; spam = true; lessThanNMessagesF = 1.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "You won a prize"; spam = true; lessThanNMessagesF = 1.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "Earn thousands daily"; spam = true; lessThanNMessagesF = 1.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "Limited offer act now"; spam = true; lessThanNMessagesF = 1.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "Casino bonus jackpot"; spam = true; lessThanNMessagesF = 1.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "Weight loss guaranteed"; spam = true; lessThanNMessagesF = 1.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "Investment opportunity guaranteed returns"; spam = true; lessThanNMessagesF = 1.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "Crypto pump signal join now"; spam = true; lessThanNMessagesF = 1.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "77"; spam = true; lessThanNMessagesF = 1.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "2222222"; spam = true; lessThanNMessagesF = 1.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "7777777"; spam = true; lessThanNMessagesF = 1.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "3333333"; spam = true; lessThanNMessagesF = 1.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "66666666"; spam = true; lessThanNMessagesF = 1.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "Click this link for free stuff"; spam = true; lessThanNMessagesF = 1.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    // Ham examples
    { text = "Hello how are you"; spam = false; lessThanNMessagesF = 0.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "Can someone help with F#"; spam = false; lessThanNMessagesF = 0.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "I have a question about dotnet"; spam = false; lessThanNMessagesF = 0.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "Thanks for the answer"; spam = false; lessThanNMessagesF = 0.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "Good morning everyone"; spam = false; lessThanNMessagesF = 0.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "Does anyone know a good library"; spam = false; lessThanNMessagesF = 0.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "The build is failing on CI"; spam = false; lessThanNMessagesF = 0.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "a"; spam = false; lessThanNMessagesF = 0.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "b"; spam = false; lessThanNMessagesF = 0.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "1"; spam = false; lessThanNMessagesF = 0.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "hello"; spam = false; lessThanNMessagesF = 0.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "How to use pattern matching"; spam = false; lessThanNMessagesF = 0.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "What is the difference between let and do"; spam = false; lessThanNMessagesF = 0.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "I fixed the bug in the parser"; spam = false; lessThanNMessagesF = 0.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
    { text = "33"; spam = false; lessThanNMessagesF = 0.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
|]

// Texts to predict
let testTexts = [| "77"; "33"; "2222222"; "7777777"; "3333333"; "66666666"; "a"; "hello"; "b"; "1" |]

open Microsoft.ML.Trainers

let trainAndPredict (seed: Nullable<int>) (singleThread: bool) (label: string) =
    let mlContext = MLContext(seed)
    let dataView = mlContext.Data.LoadFromEnumerable trainingData
    let split = mlContext.Data.TrainTestSplit(dataView, testFraction = 0.2, seed = seed)

    let featurePipeline =
        mlContext.Transforms.Text
            .FeaturizeText(outputColumnName = "TextFeaturized", inputColumnName = "text")
            .Append(mlContext.Transforms.Concatenate(
                outputColumnName = "Features",
                inputColumnNames = [| "TextFeaturized"; "lessThanNMessagesF"; "moreThanNEmojisF" |]
            ))

    let pipeline =
        if singleThread then
            let options = SdcaLogisticRegressionBinaryTrainer.Options(
                LabelColumnName = "spam",
                FeatureColumnName = "Features",
                MaximumNumberOfIterations = Nullable 50,
                NumberOfThreads = Nullable 1
            )
            featurePipeline.Append(mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(options))
        else
            featurePipeline.Append(mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(
                labelColumnName = "spam",
                featureColumnName = "Features",
                maximumNumberOfIterations = Nullable 50
            ))

    let model = pipeline.Fit(split.TrainSet)
    let engine = mlContext.Model.CreatePredictionEngine<SpamOrHam, Prediction>(model)

    printfn ""
    printfn "=== %s ===" label
    let scores =
        testTexts
        |> Array.map (fun text ->
            let pred = engine.Predict { text = text; spam = false; lessThanNMessagesF = 1.0f; moreThanNEmojisF = 0.0f; weight = 1.0f }
            printfn "  %-12s -> Score: %f" text pred.Score
            text, pred.Score
        )
    scores

// ============================================================
// Test 1: Multi-threaded SDCA with seed (current production behavior)
// ============================================================
let scores1 = trainAndPredict (Nullable 42) false "Run 1 (seed=42, multi-thread)"
let scores2 = trainAndPredict (Nullable 42) false "Run 2 (seed=42, multi-thread)"

// ============================================================
// Test 2: Single-threaded SDCA with seed (hypothesis: this is deterministic)
// ============================================================
let scores3 = trainAndPredict (Nullable 42) true "Run 3 (seed=42, single-thread)"
let scores4 = trainAndPredict (Nullable 42) true "Run 4 (seed=42, single-thread)"

// ============================================================
// Test 3: Single-threaded SDCA without seed
// ============================================================
let scores5 = trainAndPredict (Nullable<int>()) true "Run 5 (no seed, single-thread)"
let scores6 = trainAndPredict (Nullable<int>()) true "Run 6 (no seed, single-thread)"

// Compare
printfn ""
printfn "=== COMPARISON ==="
printfn ""
printfn "%-12s  %-18s  %-18s  %-18s  %-18s  %-18s  %-18s" "Text" "MT+Seed R1" "MT+Seed R2" "ST+Seed R3" "ST+Seed R4" "ST-NoSeed R5" "ST-NoSeed R6"
printfn "%s" (String('-', 120))

let mutable mtSeededMatch = true
let mutable stSeededMatch = true
let mutable stUnseededMatch = true
for i in 0 .. testTexts.Length - 1 do
    let text = testTexts[i]
    let s1 = snd scores1[i]
    let s2 = snd scores2[i]
    let s3 = snd scores3[i]
    let s4 = snd scores4[i]
    let s5 = snd scores5[i]
    let s6 = snd scores6[i]
    if s1 <> s2 then mtSeededMatch <- false
    if s3 <> s4 then stSeededMatch <- false
    if s5 <> s6 then stUnseededMatch <- false
    printfn "%-12s  %-18f  %-18f  %-18f  %-18f  %-18f  %-18f" text s1 s2 s3 s4 s5 s6

printfn ""
printfn "RESULT: Multi-thread + seed (1 vs 2):   %s" (if mtSeededMatch then "✅ MATCH" else "❌ DIFFER")
printfn "RESULT: Single-thread + seed (3 vs 4):  %s" (if stSeededMatch then "✅ MATCH" else "❌ DIFFER")
printfn "RESULT: Single-thread no seed (5 vs 6): %s" (if stUnseededMatch then "✅ MATCH" else "❌ DIFFER")

printfn ""
if stSeededMatch && not mtSeededMatch then
    printfn "🎯 DIAGNOSIS: Multi-threading in SDCA causes non-determinism."
    printfn "   FIX: Set NumberOfThreads = 1 in SdcaLogisticRegression options."
elif stSeededMatch then
    printfn "✅ Single-threaded + seed is deterministic."
else
    printfn "❌ Even single-threaded SDCA is non-deterministic — deeper issue."
