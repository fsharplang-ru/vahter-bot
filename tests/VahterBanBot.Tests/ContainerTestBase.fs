module VahterBanBot.Tests.ContainerTestBase

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open DotNet.Testcontainers.Builders
open Npgsql
open Telegram.Bot.Types
open BotTestInfra
open VahterBanBot.Types
open VahterBanBot.Utils
open BotInfra
open BotInfra.TelegramHelpers
open Xunit
open Dapper

// Telegram.Bot.Types.Message is shadowed by VahterBanBot.Types.Message
type TgMsg = Telegram.Bot.Types.Message

/// Lightweight DTO for test assertions on message events.
[<CLIMutable>]
type TestMessage =
    { chat_id: int64
      message_id: int
      user_id: int64
      text: string
      raw_message: string
      created_at: DateTime }

module private VahterTestConfig =
    let secret = "OUR_SECRET"
    let fakeAzureAlias = "fake-azure-ocr"

    let makeConfig (mlEnabled: bool) : BotContainerConfig =
        let envVars = [
            "BOT_TELEGRAM_TOKEN", "123:456"
            "BOT_AUTH_TOKEN", secret
            "IGNORE_SIDE_EFFECTS", "false"
            "USE_POLLING", "false"
            "TELEGRAM_API_URL", "http://fake-tg-api:8080"
        ]
        let envVars =
            if mlEnabled then
                envVars @ [
                    "AZURE_OCR_KEY", "secret-ocr-key"
                    "AZURE_OPENAI_KEY", "fake-llm-key"
                ]
            else envVars

        { BotProject = "VahterBanBot"
          MigrationsSubdir = "vahter-bot"
          DbName = "vahter_db"
          DbUser = "vahter_bot_ban_service"
          DbPassword = "vahter_bot_ban_service"
          AppImageName = "vahter-bot-ban-test"
          OcrEnabled = mlEnabled
          SecretToken = secret
          WebhookRoute = "/bot"
          AppEnvVars = envVars }

/// Path to the pre-trained ML model fixture committed to the repo.
/// On CI, this file MUST exist or the test run aborts immediately.
/// Locally, if missing, the first ML test run trains via the prod pipeline and writes the file.
let mlModelFixturePath =
    Path.Combine(CommonDirectoryPath.GetCallerFileDirectory().DirectoryPath, "ml-model.bin")

let private isCi =
    let v = Environment.GetEnvironmentVariable "CI"
    not (String.IsNullOrEmpty v)
    && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v = "1")

[<AbstractClass>]
type VahterTestContainers(mlEnabled: bool) =
    inherit BotContainerBase(VahterTestConfig.makeConfig mlEnabled)

    override this.SeedDatabase(connString: string) =
        task {
            let solutionDir = CommonDirectoryPath.GetSolutionDirectory()

            // seed test data
            let script = File.ReadAllText(CommonDirectoryPath.GetCallerFileDirectory().DirectoryPath + "/test_seed.sql")
            // We need to run the seed SQL via psql in the DB container, but we only have the public
            // connection string here. Use it directly with Npgsql instead.
            use conn = new NpgsqlConnection(connString)
            do! conn.OpenAsync()
            let! _ = conn.ExecuteAsync(script)
            ()

            // seed bot_setting -- common settings + per-fixture overrides
            let commonSettings = [
                "BOT_USER_ID",               "1337",                                    "FREE_FORM",    "BOT"
                "BOT_USER_NAME",             "test_bot",                                "FREE_FORM",    "BOT"
                "POTENTIAL_SPAM_CHANNEL_ID", "-101",                                    "FREE_FORM",    "CHANNELS"
                "DETECTED_SPAM_CHANNEL_ID",  "-102",                                    "FREE_FORM",    "CHANNELS"
                "ALL_LOGS_CHANNEL_ID",       "-103",                                    "FREE_FORM",    "CHANNELS"
                "DETECTED_SPAM_CLEANUP_AGE_HOURS", "24",                                "FREE_FORM",    "CHANNELS"
                "CHATS_TO_MONITOR",          """{"pro.hell":"-666","dotnetru":-42}""",  "JSON_BLOB",    "CHANNELS"
                "ALLOWED_USERS",             """{"vahter_1":"34","vahter_2":69}""",     "JSON_BLOB",    "CHANNELS"
                "UPDATE_CHAT_ADMINS",        "true",                                    "FEATURE_FLAG", "CLEANUP"
                "UPDATE_CHAT_ADMINS_INTERVAL_SEC", "86400",                             "FREE_FORM",    "CLEANUP"
            ]
            let mlSettings =
                if mlEnabled then [
                    "ML_ENABLED",                          "true",  "FEATURE_FLAG", "ML"
                    "ML_SEED",                             "42",    "FREE_FORM",    "ML"
                    "ML_TRAIN_RANDOM_SORT_DATA",           "false", "FEATURE_FLAG", "ML"
                    "ML_SPAM_THRESHOLD",                   "1.0",   "FREE_FORM",    "ML"
                    "ML_STOP_WORDS_IN_CHATS",              """{"-42":["2"]}""", "JSON_BLOB", "ML"
                    "ML_SPAM_DELETION_ENABLED",            "true",  "FEATURE_FLAG", "ML_SPAM_DELETION"
                    "ML_SPAM_AUTOBAN_ENABLED",             "true",  "FEATURE_FLAG", "ML_SPAM_AUTOBAN"
                    "ML_SPAM_AUTOBAN_CHECK_LAST_MSG_COUNT","10",    "FREE_FORM",    "ML_SPAM_AUTOBAN"
                    "ML_SPAM_AUTOBAN_SCORE_THRESHOLD",     "-4.0",  "FREE_FORM",    "ML_SPAM_AUTOBAN"
                    "OCR_ENABLED",                         "true",  "FEATURE_FLAG", "OCR"
                    "OCR_MAX_FILE_SIZE_BYTES",             (20L * 1024L * 1024L).ToString(), "FREE_FORM", "OCR"
                    "AZURE_OCR_ENDPOINT",                  $"http://{VahterTestConfig.fakeAzureAlias}:8081", "FREE_FORM", "OCR"
                    "REACTION_SPAM_ENABLED",               "true",  "FEATURE_FLAG", "REACTION_SPAM"
                    "REACTION_SPAM_MIN_MESSAGES",          "3",     "FREE_FORM",    "REACTION_SPAM"
                    "REACTION_SPAM_MAX_REACTIONS",         "5",     "FREE_FORM",    "REACTION_SPAM"
                    "FORWARD_SPAM_DETECTION_ENABLED",      "true",  "FEATURE_FLAG", "FORWARD_SPAM"
                    "INLINE_KEYBOARD_SPAM_DETECTION_ENABLED","true","FEATURE_FLAG", "INLINE_KEYBOARD_SPAM"
                    "ML_OLD_USER_MSG_COUNT",               "10",    "FREE_FORM",    "ML"
                    "LLM_TRIAGE_ENABLED",                  "true",  "FEATURE_FLAG", "LLM"
                    "AZURE_OPENAI_ENDPOINT",               $"http://{VahterTestConfig.fakeAzureAlias}:8081", "FREE_FORM", "LLM"
                ] else [
                    // these two default to true in code, so must be explicitly set to false
                    "FORWARD_SPAM_DETECTION_ENABLED",        "false", "FEATURE_FLAG", "FORWARD_SPAM"
                    "INLINE_KEYBOARD_SPAM_DETECTION_ENABLED","false", "FEATURE_FLAG", "INLINE_KEYBOARD_SPAM"
                ]
            for (key, value, typ, group) in commonSettings @ mlSettings do
                do! conn.ExecuteAsync(
                        "INSERT INTO bot_setting(key,value,type,feature_group) VALUES(@k,@v,@t,@g)",
                        {| k = key; v = value; t = typ; g = group |})
                    :> Task
        }

    member this.Http = this.BotHttp
    member this.Uri = this.BotHttp.BaseAddress

    member this.SetOcrText(text: string) = task {
        let ocrJson =
            $"""{{"modelVersion":"2023-10-01","metadata":{{"width":1020,"height":638}},"readResult":{{"blocks":[{{"lines":[{{"text":"{text}","boundingPolygon":[{{"x":1,"y":24}},{{"x":1005,"y":27}},{{"x":1004,"y":377}},{{"x":0,"y":371}}],"words":[{{"text":"{text}","confidence":0.9}}]}}]}}]}}}}"""
        this.SetAzureOcrResponse(200, ocrJson) |> Async.AwaitTask |> Async.RunSynchronously
    }

    member this.SendMessage(update: Update) = task {
        let json = JsonSerializer.Serialize(update, options = telegramJsonOptions)
        return! this.SendMessage(json)
    }

    member this.SendMessage(json: string) = task {
        let content = new StringContent(json, Encoding.UTF8, "application/json")
        let! resp = this.BotHttp.PostAsync("/bot", content)
        return resp
    }

    member _.Vahters = [
        Tg.user(id = 34, username = "vahter_1")
        Tg.user(id = 69, username = "vahter_2")
    ]

    member _.Admins = [
        Tg.user(id = 42, username = "just_admin")
    ]

    member _.PotentialSpamChannel = Tg.chat(id = -101, username = "potential_spam_channel")
    member _.DetectedSpamChannel = Tg.chat(id = -102, username = "detected_spam_channel")
    member _.AllLogsChannel = Tg.chat(id = -103, username = "all_logs_channel")
    member _.ChatsToMonitor = [
        Tg.chat(id = -666, username = "pro.hell")
        Tg.chat(id = -42, username = "dotnetru")
    ]

    member this.TryGetDbMessage(msg: TgMsg) = task {
        use conn = new NpgsqlConnection(this.DbConnectionString)
        //language=postgresql
        let sql =
            """
SELECT (data->>'chatId')::BIGINT AS chat_id,
       (data->>'messageId')::INT  AS message_id,
       (data->>'userId')::BIGINT  AS user_id,
       data->>'text'              AS text,
       (data->>'rawMessage')::JSONB::TEXT AS raw_message,
       created_at
FROM event
WHERE event_type = 'MessageReceived'
  AND (data->>'chatId')::BIGINT = @chatId
  AND (data->>'messageId')::INT = @messageId
            """
        let! dbMessage = conn.QueryAsync<TestMessage>(sql, {| chatId = msg.Chat.Id; messageId = msg.MessageId |})
        return dbMessage |> Seq.tryHead
    }

    member this.MessageBanned(msg: TgMsg) = task {
        use conn = new NpgsqlConnection(this.DbConnectionString)
        //language=postgresql
        let sql =
            """
SELECT EXISTS (
    SELECT 1 FROM event ub
    WHERE ub.event_type = 'UserBanned'
      AND (
          (ub.data->'bannedBy'->>'chatId')::BIGINT = @chatId
          AND (ub.data->'bannedBy'->>'messageId')::INT = @messageId
        OR
          (ub.data->>'chatId')::BIGINT = @chatId
          AND (ub.data->>'messageId')::INT = @messageId
      )
      AND NOT EXISTS (
          SELECT 1 FROM event uub
          WHERE uub.event_type = 'UserUnbanned'
            AND uub.stream_id = ub.stream_id
            AND uub.id > ub.id
      )
)
            """
        return! conn.QuerySingleAsync<bool>(sql, {| chatId = msg.Chat.Id; messageId = msg.MessageId |})
    }

    member this.UserBanned(userId: int64) = task {
        use conn = new NpgsqlConnection(this.DbConnectionString)
        //language=postgresql
        let sql =
            """
SELECT COUNT(*) FROM event
WHERE stream_id   = 'user:' || @userId
  AND event_type  = 'UserBanned'
  AND (data->'actor'->>'Case' = 'User'
       OR data->'bannedBy'->>'Case' = 'BannedByVahter')
            """
        let! count = conn.QuerySingleAsync<int>(sql, {| userId = userId |})
        return count > 0
    }

    member this.MessageEditedRecorded(chatId: int64, messageId: int) = task {
        use conn = new NpgsqlConnection(this.DbConnectionString)
        //language=postgresql
        let sql =
            """
SELECT COUNT(*) FROM event
WHERE event_type = 'MessageEdited'
  AND (data->>'chatId')::BIGINT = @chatId
  AND (data->>'messageId')::INT  = @messageId
            """
        let! count = conn.QuerySingleAsync<int>(sql, {| chatId = chatId; messageId = messageId |})
        return count > 0
    }

    member this.MessageIsAutoDeleted(msg: TgMsg) = task {
        use conn = new NpgsqlConnection(this.DbConnectionString)
        //language=postgresql
        let sql =
            """
SELECT COUNT(*) FROM event
WHERE event_type = 'BotAutoDeleted'
  AND (data->>'chatId')::BIGINT   = @chatId
  AND (data->>'messageId')::INT   = @messageId
            """
        let! count = conn.QuerySingleAsync<int>(sql, {| chatId = msg.Chat.Id; messageId = msg.MessageId |})
        return count > 0
    }

    member this.GetCallbackId(msg: TgMsg) (caseName: string) = task {
        //language=postgresql
        let sql = """
SELECT REPLACE(stream_id, 'callback:', '')::UUID
FROM event
WHERE event_type = 'CallbackCreated'
  AND stream_id LIKE 'callback:%'
  AND (data->>'data')::JSONB ->> 'Case' = @caseName
  AND (data->>'data')::JSONB -> 'Fields' -> 0 -> 'message' ->> 'message_id' = @messageId::TEXT
  AND (data->>'data')::JSONB -> 'Fields' -> 0 -> 'message' -> 'chat' ->> 'id' = @chatId::TEXT
"""
        let param = {| chatId = msg.Chat.Id; messageId = msg.MessageId; caseName = caseName |}
        // Retry to handle transient DB visibility delays on ARM
        let mutable result = None
        let mutable attempt = 0
        while result.IsNone && attempt < 5 do
            use conn = new NpgsqlConnection(this.DbConnectionString)
            let! rows = conn.QueryAsync<Guid>(sql, param)
            match rows |> Seq.tryHead with
            | Some id -> result <- Some id
            | None ->
                attempt <- attempt + 1
                do! Task.Delay 200
        match result with
        | Some id -> return id
        | None -> return failwith $"Callback not found for message {msg.MessageId} in chat {msg.Chat.Id} with case {caseName}"
    }

    member this.IsMessageFalsePositive(msg: TgMsg) = task {
        if String.IsNullOrWhiteSpace(msg.Text) then return false else

        use conn = new NpgsqlConnection(this.DbConnectionString)
        //language=postgresql
        let sql =
            """
SELECT EXISTS (
    SELECT 1 FROM event
    WHERE event_type = 'MessageMarkedHam'
      AND data->>'text' = @text
)
            """
        return! conn.QuerySingleAsync<bool>(sql, {| text = msg.Text |})
    }

    member this.UserBannedByBot(userId: int64) = task {
        use conn = new NpgsqlConnection(this.DbConnectionString)
        //language=postgresql
        let sql =
            """
SELECT COUNT(*) FROM event
WHERE stream_id  = 'user:' || @userId
  AND event_type = 'UserBanned'
  AND (data->'actor'->>'Case' IN ('Bot', 'ML')
       OR data->'bannedBy'->>'Case' = 'BannedByAutoBan')
            """
        let! count = conn.QuerySingleAsync<int>(sql, {| userId = userId |})
        return count > 0
    }

    member this.UserBannedByAI(userId: int64) = task {
        use conn = new NpgsqlConnection(this.DbConnectionString)
        //language=postgresql
        let sql =
            """
SELECT COUNT(*) FROM event
WHERE stream_id  = 'user:' || @userId
  AND event_type = 'UserBanned'
  AND (data->'actor'->>'Case' = 'LLM'
       OR data->'bannedBy'->>'Case' = 'BannedByAI')
            """
        let! count = conn.QuerySingleAsync<int>(sql, {| userId = userId |})
        return count > 0
    }

    member this.GetUserReactionCount(userId: int64) = task {
        use conn = new NpgsqlConnection(this.DbConnectionString)
        //language=postgresql
        let sql =
            """
SELECT COALESCE(SUM((data->>'delta')::INT), 0)::INT
FROM event
WHERE stream_id  = 'user:' || @userId
  AND event_type = 'UserReactionRecorded'
            """
        let! result = conn.QuerySingleAsync<int>(sql, {| userId = userId |})
        return result
    }

    member this.IsMessageFalseNegative(msg: TgMsg) = task {
        use conn = new NpgsqlConnection(this.DbConnectionString)
        //language=postgresql
        let sql =
            """
SELECT COUNT(*) FROM event
WHERE event_type = 'MessageMarkedSpam'
  AND (data->>'chatId')::BIGINT   = @chatId
  AND (data->>'messageId')::INT   = @messageId
            """
        let! count = conn.QuerySingleAsync<int>(sql, {| chatId = msg.Chat.Id; messageId = msg.MessageId |})
        return count > 0
    }

    /// Checks if message was auto-deleted by the bot
    member this.MessageWasDeleted(msg: TgMsg) = task {
        use conn = new NpgsqlConnection(this.DbConnectionString)
        //language=postgresql
        let sql =
            """
SELECT COUNT(*) FROM event
WHERE event_type = 'BotAutoDeleted'
  AND (data->>'chatId')::BIGINT   = @chatId
  AND (data->>'messageId')::INT   = @messageId
            """
        let! count = conn.QuerySingleAsync<int>(sql, {| chatId = msg.Chat.Id; messageId = msg.MessageId |})
        return count > 0
    }

    member this.TryGetLlmTriageVerdict(msg: TgMsg) = task {
        use conn = new NpgsqlConnection(this.DbConnectionString)
        //language=postgresql
        let sql =
            """
SELECT data->>'verdict' FROM event
WHERE event_type = 'LlmClassified'
  AND (data->>'chatId')::BIGINT   = @chatId
  AND (data->>'messageId')::INT   = @messageId
            """
        let! verdicts = conn.QueryAsync<string>(sql, {| chatId = msg.Chat.Id; messageId = msg.MessageId |})
        return verdicts |> Seq.tryHead
    }

    /// Gets the modelName from the LlmClassified event for a message (None if event absent or field missing).
    member this.TryGetLlmClassifiedModelName(msg: TgMsg) = task {
        use conn = new NpgsqlConnection(this.DbConnectionString)
        //language=postgresql
        let sql =
            """
SELECT data->>'modelName' FROM event
WHERE event_type = 'LlmClassified'
  AND (data->>'chatId')::BIGINT   = @chatId
  AND (data->>'messageId')::INT   = @messageId
            """
        let! values = conn.QueryAsync<string>(sql, {| chatId = msg.Chat.Id; messageId = msg.MessageId |})
        return values |> Seq.tryHead
    }

    /// Gets the promptHash from the LlmClassified event for a message (None if event absent or field missing).
    member this.TryGetLlmClassifiedPromptHash(msg: TgMsg) = task {
        use conn = new NpgsqlConnection(this.DbConnectionString)
        //language=postgresql
        let sql =
            """
SELECT data->>'promptHash' FROM event
WHERE event_type = 'LlmClassified'
  AND (data->>'chatId')::BIGINT   = @chatId
  AND (data->>'messageId')::INT   = @messageId
            """
        let! values = conn.QueryAsync<string>(sql, {| chatId = msg.Chat.Id; messageId = msg.MessageId |})
        return values |> Seq.tryHead
    }

    /// Gets the ML score recorded for a message via MlScoredMessage event.
    member this.GetMlScore(msg: TgMsg) = task {
        use conn = new NpgsqlConnection(this.DbConnectionString)
        //language=postgresql
        let sql =
            """
SELECT (data->>'score')::DOUBLE PRECISION FROM event
WHERE event_type = 'MlScoredMessage'
  AND (data->>'chatId')::BIGINT   = @chatId
  AND (data->>'messageId')::INT   = @messageId
            """
        let! scores = conn.QueryAsync<float>(sql, {| chatId = msg.Chat.Id; messageId = msg.MessageId |})
        return scores |> Seq.tryHead
    }

    /// Inserts a CallbackCreated event with a backdated created_at for testing orphaned cleanup.
    member this.InsertOrphanedCallback(callbackId: Guid, daysOld: int) = task {
        use conn = new NpgsqlConnection(this.DbConnectionString)
        //language=postgresql
        let sql =
            """
INSERT INTO event(stream_id, stream_version, data, created_at)
VALUES ('callback:' || @callbackId, 1,
        jsonb_build_object('Case', 'CallbackCreated',
                           'data', 'test', 'targetUserId', 0, 'actionChannelId', 0),
        @now - make_interval(days => @daysOld))
            """
        let! _ = conn.ExecuteAsync(sql, {| callbackId = callbackId; daysOld = daysOld; now = DateTime.UtcNow |})
        return ()
    }

    /// Runs the same orphaned callback cleanup as DB.expireOrphanedCallbacks.
    member this.CleanupOrphanedCallbacks(howOld: TimeSpan) = task {
        use conn = new NpgsqlConnection(this.DbConnectionString)
        //language=postgresql
        let findSql =
            """
SELECT REPLACE(stream_id, 'callback:', '')::UUID
FROM event
WHERE event_type = 'CallbackCreated'
  AND created_at < @cutoff
  AND NOT EXISTS (
      SELECT 1 FROM event e2
      WHERE e2.stream_id = event.stream_id
        AND e2.event_type IN ('CallbackResolved', 'CallbackExpired')
  )
            """
        let! orphanedIds = conn.QueryAsync<Guid>(findSql, {| cutoff = DateTime.UtcNow.Subtract howOld |})
        let ids = Array.ofSeq orphanedIds
        //language=postgresql
        let expireSql =
            """
INSERT INTO event(stream_id, stream_version, data)
VALUES ('callback:' || @callbackId,
        (SELECT MAX(stream_version) FROM event WHERE stream_id = 'callback:' || @callbackId) + 1,
        '{"Case":"CallbackExpired"}'::JSONB)
ON CONFLICT (stream_id, stream_version) DO NOTHING
            """
        for id in ids do
            let! _ = conn.ExecuteAsync(expireSql, {| callbackId = id |})
            ()
        return ids.Length
    }

    /// Checks if a CallbackExpired event exists for the given callback.
    member this.HasCallbackExpired(callbackId: Guid) = task {
        use conn = new NpgsqlConnection(this.DbConnectionString)
        //language=postgresql
        let sql =
            """
SELECT COUNT(*) FROM event
WHERE stream_id = 'callback:' || @callbackId AND event_type = 'CallbackExpired'
            """
        let! count = conn.ExecuteScalarAsync<int>(sql, {| callbackId = callbackId |})
        return count > 0
    }

    member this.SetBotSetting(key: string, value: string) = task {
        use conn = new NpgsqlConnection(this.DbConnectionString)
        //language=postgresql
        let sql =
            """
INSERT INTO bot_setting(key, value, type, feature_group)
VALUES(@key, @value, 'FREE_FORM', 'RUNTIME')
ON CONFLICT (key) DO UPDATE SET value = @value
            """
        let! _ = conn.ExecuteAsync(sql, {| key = key; value = value |})
        return ()
    }

    member this.ReloadSettings() = task {
        let! resp = this.BotHttp.PostAsync("/reload-settings", null)
        resp.EnsureSuccessStatusCode() |> ignore
    }

/// Polls `/ready` until the bot reports its ML model is loaded or trained.
/// Preload path: ready in <1s. Fresh-train path (first local run): up to ~3 minutes.
let private waitForReady (http: HttpClient) (timeout: TimeSpan) : Task<unit> = task {
    let pollInterval = TimeSpan.FromMilliseconds 500.0
    let deadline = DateTime.UtcNow.Add timeout
    let mutable ready = false
    while not ready && DateTime.UtcNow < deadline do
        try
            let! resp = http.GetAsync("/ready")
            if resp.IsSuccessStatusCode then
                ready <- true
            else
                do! Task.Delay pollInterval
        with _ ->
            do! Task.Delay pollInterval
    if not ready then
        failwithf "Bot /ready did not return 200 within %A — ML model failed to train or load" timeout
}

type MlEnabledVahterTestContainers() =
    inherit VahterTestContainers(mlEnabled = true)

    override this.SeedDatabase(connString: string) =
        // F# disallows `base` references inside computation expressions, so kick off the base
        // call eagerly and await its hot Task inside the CE.
        let baseSeed = base.SeedDatabase(connString)
        task {
            do! baseSeed

            // Preload the pre-trained ML model fixture so the bot's StartAsync skips training.
            // SDCA training is non-deterministic across CPU architectures (Windows/x86_64 vs
            // Linux/ARM64) — vectorized FP arithmetic differs in reduction order, and SGD
            // compounds tiny per-step deltas into large weight differences. Pinning a model
            // binary keeps test scoring stable across platforms.
            //
            // CI: file MUST exist; missing file aborts the test run.
            // Local: file optional. If absent, bot trains via prod pipeline; AfterStart
            //   then extracts the trained bytes back to disk so the developer can commit.
            let fixtureExists = File.Exists mlModelFixturePath
            if not fixtureExists && isCi then
                failwithf
                    "ML model fixture missing at %s. \
                     CI cannot train the model deterministically (SDCA differs across CPU architectures). \
                     Run the test suite locally once to generate the fixture, then commit ml-model.bin."
                    mlModelFixturePath
            if fixtureExists then
                use conn = new NpgsqlConnection(connString)
                do! conn.OpenAsync()
                let! bytes = File.ReadAllBytesAsync mlModelFixturePath
                let! _ =
                    conn.ExecuteAsync(
                        "INSERT INTO ml_trained_model(id, model_data, created_at) \
                         VALUES (1, @data, now()) \
                         ON CONFLICT (id) DO UPDATE SET model_data = EXCLUDED.model_data, created_at = EXCLUDED.created_at",
                        {| data = bytes |})
                ()
        }

    override this.AfterStart() =
        task {
            do! waitForReady this.BotHttp (TimeSpan.FromMinutes 3.0)

            // Local-dev convenience: if no fixture file existed at seed time, the bot just trained
            // a fresh model. Extract its bytes from DB and write them to ml-model.bin so the next
            // run uses the fast preload path AND so the developer can commit the binary for CI.
            if not (File.Exists mlModelFixturePath) then
                use conn = new NpgsqlConnection(this.DbConnectionString)
                do! conn.OpenAsync()
                use cmd = new NpgsqlCommand("SELECT model_data FROM ml_trained_model WHERE id = 1", conn)
                use! reader = cmd.ExecuteReaderAsync()
                let! hasRow = reader.ReadAsync()
                if not hasRow then
                    failwith "Expected ml_trained_model row after /ready=200 but found none"
                let bytes = reader.GetFieldValue<byte[]>(0)
                do! File.WriteAllBytesAsync(mlModelFixturePath, bytes)
                Console.Error.WriteLine
                    (sprintf "[ml-fixture] Wrote freshly trained model to %s (%d bytes). \
                              Commit this file so CI can reuse it." mlModelFixturePath bytes.Length)
        } :> Task

/// Variant that DELIBERATELY skips fixture preload to exercise the production training pipeline
/// end-to-end. Used by MLTrainingPipelineTests as a smoke test that training still produces a
/// usable model (the most important property of the bot — autonomous spam detection).
type MlTrainingFromScratchTestContainers() =
    inherit VahterTestContainers(mlEnabled = true)

    override this.AfterStart() =
        task {
            // Same /ready wait, but never extract bytes — we don't want a throwaway fresh-train
            // model to overwrite the curated ml-model.bin fixture.
            do! waitForReady this.BotHttp (TimeSpan.FromMinutes 3.0)
        } :> Task

type MlDisabledVahterTestContainers() =
    inherit VahterTestContainers(mlEnabled = false)

// Kept as a no-op so existing test classes can keep `IClassFixture<MlAwaitFixture>` unchanged.
// The actual readiness wait now lives in MlEnabledVahterTestContainers.AfterStart, which runs
// once per assembly fixture instead of once per class — and uses /ready polling instead of a
// blind 10-second sleep.
type MlAwaitFixture() =
    interface IAsyncLifetime with
        member this.InitializeAsync() = ValueTask()
    interface IAsyncDisposable with
        member this.DisposeAsync() = ValueTask()
