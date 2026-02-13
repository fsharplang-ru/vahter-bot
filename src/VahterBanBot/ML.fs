module VahterBanBot.ML

open System
open System.Diagnostics
open System.IO
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
type SpamOrHam =
    { text: string
      spam: bool
      lessThanNMessagesF: single
      moreThanNEmojisF: single
      weight: single
      createdAt: DateTime }

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
    let mutable modelCreatedAt: DateTime option = None

    /// Loads a serialized model from DB via streaming and creates a PredictionEngine.
    let loadModelFromDb () = task {
        let! result = DB.withTrainedModel (fun (stream, createdAt) -> task {
            let mlContext = MLContext(botConf.MlSeed)
            let model, _schema = mlContext.Model.Load(stream)
            predictionEngine <- Some(mlContext.Model.CreatePredictionEngine<SpamOrHam, Prediction>(model))
            modelCreatedAt <- Some createdAt
            logger.LogInformation("Loaded trained model from DB (created at {CreatedAt})", createdAt)
        })
        match result with
        | Some () -> return true
        | None ->
            logger.LogInformation "No trained model found in DB"
            return false
    }

    /// Trains the model from scratch, saves to DB, and sets the prediction engine.
    let trainAndSaveModel (sendMetrics: bool) = task {
        // switch to thread pool
        do! Task.Yield()
        logger.LogInformation "Training model..."
        
        let sw = Stopwatch.StartNew()

        let mlContext = MLContext(botConf.MlSeed)
        
        let trainDate = DateTime.UtcNow - botConf.MlTrainInterval
        let! rawData = DB.mlData botConf.MlTrainCriticalMsgCount trainDate
        
        logger.LogInformation $"Training data count: {rawData.Length}"
        
        let now = DateTime.UtcNow
        let k = botConf.MlWeightDecayK
        let data =
            rawData
            |> Array.map (fun x ->
                let w =
                    if k > 0.0 then
                        single (Math.Exp(-k * (now - x.created_at).TotalDays))
                    else
                        1.0f
                { text = x.text
                  spam = x.spam
                  createdAt = x.created_at
                  weight = w
                  moreThanNEmojisF = if x.custom_emoji_count > botConf.MlCustomEmojiThreshold then 1.0f else 0.0f
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
        
        let featurePipeline =
            mlContext.Transforms.Text
                .FeaturizeText(outputColumnName = "TextFeaturized", inputColumnName = "text")
                .Append(mlContext.Transforms.Concatenate(outputColumnName = "Features", inputColumnNames = [|"TextFeaturized"; "lessThanNMessagesF"; "moreThanNEmojisF"|]))

        let dataProcessPipeline =
            if k > 0.0 then
                featurePipeline.Append(mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(
                    labelColumnName = "spam",
                    featureColumnName = "Features",
                    exampleWeightColumnName = "weight",
                    maximumNumberOfIterations = botConf.MlMaxNumberOfIterations
                ))
            else
                featurePipeline.Append(mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(
                    labelColumnName = "spam",
                    featureColumnName = "Features",
                    maximumNumberOfIterations = botConf.MlMaxNumberOfIterations
                ))
                
        logger.LogInformation "Fitting model..."

        let trainedModel = dataProcessPipeline.Fit(trainingData)
        
        logger.LogInformation "Evaluating model..."
        
        predictionEngine <- Some(mlContext.Model.CreatePredictionEngine<SpamOrHam, Prediction>(trainedModel))
        
        let predictions = trainedModel.Transform(testData)
        let metrics = mlContext.BinaryClassification.Evaluate(data = predictions, labelColumnName = "spam", scoreColumnName = "Score")
        
        logger.LogInformation "Model transformation complete"

        // Serialize model to MemoryStream and stream directly to DB.
        // We pass the MemoryStream as a Stream parameter to Npgsql, avoiding the
        // extra byte[] copy that .ToArray() would create.
        let ms = new MemoryStream()
        try
            mlContext.Model.Save(trainedModel, dataView.Schema, ms)
            logger.LogInformation("Serialized model ({Size} bytes)", ms.Length)
            ms.Position <- 0L
            do! DB.saveTrainedModel ms
            modelCreatedAt <- Some DateTime.UtcNow
            logger.LogInformation "Saved trained model to DB"
        finally
            ms.Dispose()
        
        sw.Stop()
        
        let metricsStr = metricsToString metrics sw.Elapsed
        logger.LogInformation metricsStr
        if sendMetrics then
            do! telegramClient.SendMessage(
                    chatId = ChatId(botConf.AllLogsChannelId),
                    text = metricsStr,
                    parseMode = ParseMode.Markdown
                ) |> taskIgnore
        logger.LogInformation "Model trained and saved"
    }

    // if ML is ready (either disabled or model is trained)
    member _.IsReady = not botConf.MlEnabled || predictionEngine.IsSome

    member _.Predict(text: string, userMsgCount: int, entities: MessageEntity array) =
        try
            match predictionEngine with
            | Some predictionEngine ->
                let emojiCount =
                    entities
                    |> Option.ofObj
                    |> Option.defaultValue [||]
                    |> Seq.filter (fun x -> x.Type = MessageEntityType.CustomEmoji)
                    |> Seq.length
                
                predictionEngine.Predict
                    { text = text
                      spam = false
                      lessThanNMessagesF = if userMsgCount < botConf.MlTrainCriticalMsgCount then 1.0f else 0.0f
                      moreThanNEmojisF = if emojiCount > botConf.MlCustomEmojiThreshold then 1.0f else 0.0f
                      weight = 1.0f
                      createdAt = DateTime.UtcNow }
                |> Some
            | None ->
                logger.LogInformation "Model not trained yet"
                None
        with ex ->
            logger.LogError(ex, "Error predicting")
            None

    /// Retrains the model from scratch and saves to DB.
    /// Called by the scheduler (CleanupService) as a daily job.
    member _.RetrainAndSave() = task {
        try
            do! trainAndSaveModel true
        with ex ->
            logger.LogError(ex, "Error retraining model")
    }

    /// Checks if there's a newer model in DB and reloads if so.
    /// Called periodically by the scheduler on all pods.
    member _.TryReloadIfNewer() = task {
        try
            if botConf.MlEnabled then
                let! dbCreatedAt = DB.getModelCreatedAt()
                match dbCreatedAt, modelCreatedAt with
                | Some dbTime, Some localTime when dbTime > localTime ->
                    logger.LogInformation("Newer model found in DB (DB: {DbTime}, local: {LocalTime}), reloading...", dbTime, localTime)
                    let! _ = loadModelFromDb()
                    ()
                | Some _, None ->
                    // We don't have a model yet but DB has one (shouldn't normally happen after startup)
                    logger.LogInformation "Model found in DB but not loaded locally, loading..."
                    let! _ = loadModelFromDb()
                    ()
                | _ -> ()
        with ex ->
            logger.LogError(ex, "Error checking for newer model")
    }

    interface IHostedService with
        member this.StartAsync _ = task {
            if botConf.MlEnabled then
                // Try to load pre-trained model from DB (fast path)
                let! loaded = loadModelFromDb()
                if not loaded then
                    // No model in DB. Use advisory lock so only one pod trains.
                    // Lock key 1337 is an arbitrary constant for ML training coordination.
                    let! trained = DB.withAdvisoryLock 1337 (fun () -> task {
                        // Double-check: another pod may have saved while we waited for the lock
                        let! alreadyLoaded = loadModelFromDb()
                        if not alreadyLoaded then
                            do! trainAndSaveModel false
                    })
                    if not trained then
                        // Another pod is training. Poll DB until model appears.
                        logger.LogInformation "Another pod is training the model, waiting..."
                        let mutable loaded = false
                        let mutable attempts = 0
                        while not loaded && attempts < 60 do // up to ~5 minutes
                            do! Task.Delay(TimeSpan.FromSeconds 5L)
                            let! result = loadModelFromDb()
                            loaded <- result
                            attempts <- attempts + 1
                        if not loaded then
                            // Timeout or other pod crashed. Train ourselves as fallback.
                            logger.LogWarning "Timed out waiting for model from another pod, training locally"
                            try
                                do! trainAndSaveModel false
                            with ex ->
                                logger.LogError(ex, "Error training model on startup (fallback)")
        }

        member _.StopAsync _ = Task.CompletedTask
