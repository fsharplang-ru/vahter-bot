module VahterBanBot.ML

open System
open System.Diagnostics
open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.ML
open Microsoft.ML.Data
open Telegram.Bot
open Telegram.Bot.Types
open Telegram.Bot.Types.Enums
open VahterBanBot.DB
open VahterBanBot.Types
open VahterBanBot.Utils

[<CLIMutable>]
type Prediction =
    { Score: single
      text: string
      spam: bool }

type MachineLearning(
    logger: ILogger<MachineLearning>,
    telegramClient: ITelegramBotClient,
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
    
    let trainModel() = task {
        // switch to thread pool
        do! Task.Yield()
        
        let sw = Stopwatch.StartNew()

        let mlContext = MLContext(botConf.MlSeed)

        let! data = DB.mlData botConf.MlTrainBeforeDate
        
        let dataView = mlContext.Data.LoadFromEnumerable data
        let trainTestSplit = mlContext.Data.TrainTestSplit(dataView, testFraction = botConf.MlTrainingSetFraction)
        let trainingData = trainTestSplit.TrainSet
        let testData = trainTestSplit.TestSet
        
        let dataProcessPipeline = mlContext.Transforms.Text.FeaturizeText(outputColumnName = "Features", inputColumnName = "text")
        let trainer = mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(labelColumnName = "spam", featureColumnName = "Features")
        let trainingPipeline = dataProcessPipeline.Append(trainer)
        
        let trainedModel = trainingPipeline.Fit(trainingData)
        predictionEngine <- Some(mlContext.Model.CreatePredictionEngine<SpamOrHam, Prediction>(trainedModel))
        
        let predictions = trainedModel.Transform(testData)
        let metrics = mlContext.BinaryClassification.Evaluate(data = predictions, labelColumnName = "spam", scoreColumnName = "Score")
        
        sw.Stop()
        
        let metricsStr = metricsToString metrics sw.Elapsed
        logger.LogInformation metricsStr
        do! telegramClient.SendTextMessageAsync(ChatId(botConf.LogsChannelId), metricsStr, parseMode = ParseMode.Markdown)
            |> taskIgnore
    }

    member _.Predict(text: string) =
        try
            match predictionEngine with
            | Some predictionEngine ->
                predictionEngine.Predict({ text = text; spam = false })
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
                try
                    logger.LogInformation "Training model..."
                    do! trainModel()
                with ex ->
                    logger.LogError(ex, "Error training model")
        }

        member this.StopAsync _ = Task.CompletedTask
