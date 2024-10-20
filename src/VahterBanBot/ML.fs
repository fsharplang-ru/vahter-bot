module VahterBanBot.ML

open System
open System.Diagnostics
open System.Text
open System.Threading
open System.Threading.Tasks
open Funogram.Telegram.Types
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.ML
open Microsoft.ML.Data
open VahterBanBot.DB
open VahterBanBot.Types
open VahterBanBot.Utils

[<CLIMutable>]
type SpamOrHam =
    { text: string
      spam: bool
      lessThanNMessagesF: single
      createdAt: DateTime }

[<CLIMutable>]
type Prediction =
    { Score: single
      text: string
      spam: bool }

type MachineLearning(
    logger: ILogger<MachineLearning>,
    telegramClient: TelegramBotClient,
    botConf: BotConfiguration
) =
    let metricsToString(metrics: CalibratedBinaryClassificationMetrics) (duration: TimeSpan) =
        let sb = StringBuilder()
        let formattedTable = metrics.ConfusionMatrix.GetFormattedConfusionTable()
        %sb.AppendLine($"Model trained in {duration.TotalSeconds} seconds with following metrics:")
        %sb.AppendLine($"Accuracy: {metrics.Accuracy}")
        %sb.AppendLine($"AreaUnderPrecisionRecallCurve: {metrics.AreaUnderPrecisionRecallCurve}")
        %sb.AppendLine($"ConfusionMatrix:\n```\n{formattedTable}\n```")
        %sb.AppendLine($"Entropy: {metrics.Entropy}")
        %sb.AppendLine($"F1Score: {metrics.F1Score}")
        %sb.AppendLine($"LogLoss: {metrics.LogLoss}")
        %sb.AppendLine($"LogLossReduction: {metrics.LogLossReduction}")
        %sb.AppendLine($"NegativePrecision: {metrics.NegativePrecision}")
        %sb.AppendLine($"NegativeRecall: {metrics.NegativeRecall}")
        %sb.AppendLine($"PositivePrecision: {metrics.PositivePrecision}")
        %sb.AppendLine($"PositiveRecall: {metrics.PositiveRecall}")
        sb.ToString()
        
    let mutable predictionEngine: PredictionEngine<SpamOrHam, Prediction> option = None
    let mutable timer: Timer = null
    
    let trainModel _ = task {
        try
            // switch to thread pool
            do! Task.Yield()
            logger.LogInformation "Training model..."
            
            let sw = Stopwatch.StartNew()

            let mlContext = MLContext(botConf.MlSeed)
            
            let trainDate = DateTime.UtcNow - botConf.MlTrainInterval
            let! rawData = DB.mlData botConf.MlTrainCriticalMsgCount trainDate
            
            let data =
                rawData
                |> Array.map (fun x ->
                    { text = x.text
                      spam = x.spam
                      createdAt = x.created_at
                      lessThanNMessagesF = if x.less_than_n_messages then 1.0f else 0.0f }
                )
                |> fun x ->
                    if botConf.MlTrainRandomSortData then
                        Array.sortInPlaceBy (fun _ -> Guid.NewGuid()) x
                    x

            let dataView = mlContext.Data.LoadFromEnumerable data
            let trainTestSplit = mlContext.Data.TrainTestSplit(dataView, testFraction = botConf.MlTrainingSetFraction)
            let trainingData = trainTestSplit.TrainSet
            let testData = trainTestSplit.TestSet
            
            let dataProcessPipeline =
                mlContext.Transforms.Text
                    .FeaturizeText(outputColumnName = "TextFeaturized", inputColumnName = "text")
                    .Append(mlContext.Transforms.Concatenate(outputColumnName = "Features", inputColumnNames = [|"TextFeaturized"; "lessThanNMessagesF";|]))
                    .Append(mlContext.BinaryClassification.Trainers.LbfgsLogisticRegression(labelColumnName = "spam", featureColumnName = "Features"))

            let trainedModel = dataProcessPipeline.Fit(trainingData)
            predictionEngine <- Some(mlContext.Model.CreatePredictionEngine<SpamOrHam, Prediction>(trainedModel))
            
            let predictions = trainedModel.Transform(testData)
            let metrics = mlContext.BinaryClassification.Evaluate(data = predictions, labelColumnName = "spam", scoreColumnName = "Score")
            
            sw.Stop()
            
            let metricsStr = metricsToString metrics sw.Elapsed
            logger.LogInformation metricsStr
            do! telegramClient.SendTextMessageAsync(ChatId.Int(botConf.LogsChannelId), metricsStr, parseMode = ParseMode.Markdown)
                |> taskIgnore
        with ex ->
            logger.LogError(ex, "Error training model")
    }

    member _.Predict(text: string, userMsgCount: int) =
        try
            match predictionEngine with
            | Some predictionEngine ->
                predictionEngine.Predict
                    { text = text
                      spam = false
                      lessThanNMessagesF = if userMsgCount < botConf.MlTrainCriticalMsgCount then 1.0f else 0.0f
                      createdAt = DateTime.UtcNow }
                |> Some
            | None ->
                logger.LogInformation "Model not trained yet"
                None
        with ex ->
            logger.LogError(ex, "Error predicting")
            None

    interface IHostedService with
        member this.StartAsync _ = task {
            if botConf.MlEnabled then
                if botConf.MlRetrainInterval.IsSome then
                    // recurring
                    timer <- new Timer(TimerCallback(trainModel >> ignore), null, TimeSpan.Zero, botConf.MlRetrainInterval.Value)
                else
                    // once
                    do! trainModel()
        }

        member this.StopAsync _ =
            match timer with
            | null -> Task.CompletedTask
            | timer -> timer.DisposeAsync().AsTask()
