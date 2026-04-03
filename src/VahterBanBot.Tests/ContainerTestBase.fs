module VahterBanBot.Tests.ContainerTestBase

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Configurations
open DotNet.Testcontainers.Containers
open Npgsql
open Telegram.Bot.Types
open Testcontainers.PostgreSql
open VahterBanBot.Tests.TgMessageUtils
open VahterBanBot.Types
open VahterBanBot.Utils
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

[<AbstractClass>]
type VahterTestContainers(mlEnabled: bool) =
    let solutionDir = CommonDirectoryPath.GetSolutionDirectory()
    let dbAlias = "vahter-db"
    let internalConnectionString = $"Server={dbAlias};Database=vahter_db;Port=5432;User Id=vahter_bot_ban_service;Password=vahter_bot_ban_service;Include Error Detail=true;Minimum Pool Size=1;Maximum Pool Size=20;Max Auto Prepare=100;Auto Prepare Min Usages=1;Trust Server Certificate=true;"
    let pgImage = "postgres:15.6" // same as in Azure
    
    // will be filled in IAsyncLifetime.InitializeAsync
    let mutable uri: Uri = null
    let mutable httpClient: HttpClient = null
    let mutable publicConnectionString: string = null
    let mutable testArtifactsDir: string = null
    
    // base image for the app, we'll build exactly how we build it in Azure
    let buildLogger = StringLogger()
    let image =
        ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionDir, String.Empty)
            .WithDockerfile("./Dockerfile")
            .WithName("vahter-bot-ban-test")
            // workaround for multi-stage builds cleanup
            .WithBuildArgument("RESOURCE_REAPER_SESSION_ID", ResourceReaper.DefaultSessionId.ToString("D"))
            // it might speed up the process to not clean up the base image
            .WithCleanUp(false)
            .WithDeleteIfExists(true)
            .WithLogger(buildLogger)
            .Build()

    // private network for the containers
    let network =
        NetworkBuilder()
            .Build()

    // PostgreSQL container. Important to have the same image as in Azure
    // and assign network alias to it as it will be "host" in DB connection string for the app
    let dbContainer =
        PostgreSqlBuilder(pgImage)
            .WithNetwork(network)
            .WithNetworkAliases(dbAlias)
            .Build()

    // Flyway container to run migrations
    let flywayContainer =
        ContainerBuilder("flyway/flyway")
            .WithNetwork(network)
            .WithBindMount(CommonDirectoryPath.GetSolutionDirectory().DirectoryPath + "/src/migrations", "/flyway/sql", AccessMode.ReadOnly)
            .WithEnvironment("FLYWAY_URL", "jdbc:postgresql://vahter-db:5432/vahter_db")
            .WithEnvironment("FLYWAY_USER", "admin")
            .WithEnvironment("FLYWAY_PASSWORD", "admin")
            .WithCommand("migrate", "-schemas=public")
            .WithWaitStrategy(Wait.ForUnixContainer().AddCustomWaitStrategy(
                { new IWaitUntil  with
                    override this.UntilAsync(container) = task {
                        let! _ = container.GetExitCodeAsync()
                        return true
                    }
                }))
            .DependsOn(dbContainer)
            .Build()

    // the app container
    // secrets and dev flags are env vars; all other settings come from bot_setting (seeded in InitializeAsync)
    let appContainer =
        let builder =
            ContainerBuilder(image)
                .WithNetwork(network)
                .WithPortBinding(80, true)
                .WithEnvironment("BOT_TELEGRAM_TOKEN", "123:456")
                .WithEnvironment("BOT_AUTH_TOKEN", "OUR_SECRET")
                .WithEnvironment("IGNORE_SIDE_EFFECTS", "false")
                .WithEnvironment("USE_FAKE_API", "true")
                .WithEnvironment("USE_POLLING", "false")
                .WithEnvironment("DATABASE_URL", internalConnectionString)
                // .net 8.0 upgrade has a breaking change
                // https://learn.microsoft.com/en-us/dotnet/core/compatibility/containers/8.0/aspnet-port
                // Azure default port for containers is 80, so we need to explicitly set it
                .WithEnvironment("ASPNETCORE_HTTP_PORTS", "80")
                .DependsOn(flywayContainer)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(80))
        if mlEnabled then
            builder
                .WithEnvironment("AZURE_OCR_KEY", "secret-ocr-key")
                .WithEnvironment("AZURE_OPENAI_KEY", "fake-llm-key")
                .Build()
        else
            builder.Build()
            
    let dumpContainerLogs (containerName: string) (container: IContainer) =
        task {
            try
                let! struct (stdout, stderr) = container.GetLogsAsync()
                let dir = testArtifactsDir
                if not (isNull dir) then
                    Directory.CreateDirectory(dir) |> ignore
                    let path = Path.Combine(dir, $"{containerName}.log")
                    let content = $"=== STDOUT ===\n{stdout}\n=== STDERR ===\n{stderr}\n"
                    File.WriteAllText(path, content)
                return (stdout, stderr)
            with ex ->
                eprintfn $"Failed to get logs for {containerName}: {ex.Message}"
                return ("", "")
        }

    let startContainers() = task {
        try
            // start building the image and spin up db at the same time
            let imageTask = image.CreateAsync()
            let dbTask = dbContainer.StartAsync()

            // wait for both to finish
            do! imageTask
            do! dbTask            
        with
        | e ->
            let logs = buildLogger.ExtractMessages()
            let errorMessage = "Container startup failure, logs:\n" + if String.IsNullOrWhiteSpace logs then "<no logs provided>" else logs
            raise <| Exception(errorMessage, e)
    }

    interface IAsyncLifetime with
        member this.InitializeAsync() = ValueTask(task {
            testArtifactsDir <- Path.Combine(solutionDir.DirectoryPath, "test-artifacts", this.GetType().Name)
            try
                do! startContainers()
                publicConnectionString <- $"Server=127.0.0.1;Database=vahter_db;Port={dbContainer.GetMappedPublicPort(5432)};User Id=vahter_bot_ban_service;Password=vahter_bot_ban_service;Include Error Detail=true;Minimum Pool Size=1;Maximum Pool Size=20;Max Auto Prepare=100;Auto Prepare Min Usages=1;Trust Server Certificate=true;"
                
                // initialize DB with the schema, database and a DB user
                let script = File.ReadAllText(CommonDirectoryPath.GetSolutionDirectory().DirectoryPath + "/init.sql")
                let! initResult = dbContainer.ExecScriptAsync(script)
                if initResult.Stderr <> "" then
                    failwith initResult.Stderr

                // run migrations
                do! flywayContainer.StartAsync()
                let! out, err = flywayContainer.GetLogsAsync()
                if err <> "" then
                    failwith err
                if not (out.Contains "Successfully applied") then
                    failwith out
                
                // seed some test data
                let script = File.ReadAllText(CommonDirectoryPath.GetCallerFileDirectory().DirectoryPath + "/test_seed.sql")
                let scriptFilePath = String.Join("/", String.Empty, "tmp", Guid.NewGuid().ToString("D"), Path.GetRandomFileName())
                do! dbContainer.CopyAsync(Encoding.Default.GetBytes script, scriptFilePath, fileMode = Unix.FileMode644)
                let! scriptResult = dbContainer.ExecAsync [|"psql"; "--username"; "vahter_bot_ban_service"; "--dbname"; "vahter_db"; "--file"; scriptFilePath |]

                if scriptResult.Stderr <> "" then
                    failwith scriptResult.Stderr

                // seed bot_setting — common settings + per-fixture overrides
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
                        "AZURE_OCR_ENDPOINT",                  "https://fake-azure-ocr.cognitiveservices.azure.com/ocr", "FREE_FORM", "OCR"
                        "REACTION_SPAM_ENABLED",               "true",  "FEATURE_FLAG", "REACTION_SPAM"
                        "REACTION_SPAM_MIN_MESSAGES",          "3",     "FREE_FORM",    "REACTION_SPAM"
                        "REACTION_SPAM_MAX_REACTIONS",         "5",     "FREE_FORM",    "REACTION_SPAM"
                        "FORWARD_SPAM_DETECTION_ENABLED",      "true",  "FEATURE_FLAG", "FORWARD_SPAM"
                        "INLINE_KEYBOARD_SPAM_DETECTION_ENABLED","true","FEATURE_FLAG", "INLINE_KEYBOARD_SPAM"
                        "LLM_TRIAGE_ENABLED",                  "true",  "FEATURE_FLAG", "LLM"
                        "AZURE_OPENAI_ENDPOINT",               "https://fake-azure-openai.openai.azure.com", "FREE_FORM", "LLM"
                    ] else [
                        // these two default to true in code, so must be explicitly set to false
                        "FORWARD_SPAM_DETECTION_ENABLED",        "false", "FEATURE_FLAG", "FORWARD_SPAM"
                        "INLINE_KEYBOARD_SPAM_DETECTION_ENABLED","false", "FEATURE_FLAG", "INLINE_KEYBOARD_SPAM"
                    ]
                use settingsConn = new NpgsqlConnection(publicConnectionString)
                for (key, value, typ, group) in commonSettings @ mlSettings do
                    do! settingsConn.ExecuteAsync(
                            "INSERT INTO bot_setting(key,value,type,feature_group) VALUES(@k,@v,@t,@g)",
                            {| k = key; v = value; t = typ; g = group |})
                        :> Task

                // start the app container
                do! appContainer.StartAsync()
                
                // initialize the http client with correct hostname and port
                httpClient <- new HttpClient()
                uri <- Uri($"http://{appContainer.Hostname}:{appContainer.GetMappedPublicPort(80)}")
                httpClient.BaseAddress <- uri
                httpClient.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", "OUR_SECRET")
            finally
                if appContainer.State <> TestcontainersStates.Undefined then
                    let struct (_stdout, err) = appContainer.GetLogsAsync().Result
                    if err <> "" then
                        failwith err
        })
    interface IAsyncDisposable with
        member this.DisposeAsync() = ValueTask(task {
            // Dump logs FIRST — logs become inaccessible after container removal
            let! _ = dumpContainerLogs "postgres" dbContainer
            let! _ = dumpContainerLogs "flyway" flywayContainer
            let! _ = dumpContainerLogs "app" appContainer
            // stop all the containers, flyway might be dead already
            do! flywayContainer.DisposeAsync()
            do! appContainer.DisposeAsync()
            do! dbContainer.DisposeAsync()
            // do! image.DisposeAsync() // might be faster not to dispose base image to cache?
        })

    member _.Http = httpClient
    member _.Uri = uri
    
    member this.SendMessage(update: Update) = task {
        let json = JsonSerializer.Serialize(update, options = jsonOptions)
        return! this.SendMessage(json)
    }

    member _.SendMessage(json: string) = task {
        let content = new StringContent(json, Encoding.UTF8, "application/json")
        let! resp = httpClient.PostAsync("/bot", content)
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

    member _.TryGetDbMessage(msg: TgMsg) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
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

    member _.MessageBanned(msg: TgMsg) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
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

    member _.UserBanned(userId: int64) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
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

    member _.MessageEditedRecorded(chatId: int64, messageId: int) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
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

    member _.MessageIsAutoDeleted(msg: TgMsg) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
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
    
    member _.GetCallbackId(msg: TgMsg) (caseName: string) = task {
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
            use conn = new NpgsqlConnection(publicConnectionString)
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
    
    member _.IsMessageFalsePositive(msg: TgMsg) = task {
        if String.IsNullOrWhiteSpace(msg.Text) then return false else

        use conn = new NpgsqlConnection(publicConnectionString)
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

    member _.UserBannedByBot(userId: int64) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
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

    member _.UserBannedByAI(userId: int64) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
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
    
    member _.GetUserReactionCount(userId: int64) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
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
    
    member _.IsMessageFalseNegative(msg: TgMsg) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
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
    member _.MessageWasDeleted(msg: TgMsg) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
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
        use conn = new NpgsqlConnection(publicConnectionString)
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
    member _.TryGetLlmClassifiedModelName(msg: TgMsg) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
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
    member _.TryGetLlmClassifiedPromptHash(msg: TgMsg) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
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
    member _.GetMlScore(msg: TgMsg) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
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
    member _.InsertOrphanedCallback(callbackId: Guid, daysOld: int) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
        //language=postgresql
        let sql =
            """
INSERT INTO event(stream_id, stream_version, data, created_at)
VALUES ('callback:' || @callbackId, 1,
        jsonb_build_object('Case', 'CallbackCreated',
                           'data', 'test', 'targetUserId', 0, 'actionChannelId', 0),
        NOW() - make_interval(days => @daysOld))
            """
        let! _ = conn.ExecuteAsync(sql, {| callbackId = callbackId; daysOld = daysOld |})
        return ()
    }

    /// Runs the same orphaned callback cleanup as DB.expireOrphanedCallbacks.
    member _.CleanupOrphanedCallbacks(howOld: TimeSpan) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
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
    member _.HasCallbackExpired(callbackId: Guid) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
        //language=postgresql
        let sql =
            """
SELECT COUNT(*) FROM event
WHERE stream_id = 'callback:' || @callbackId AND event_type = 'CallbackExpired'
            """
        let! count = conn.ExecuteScalarAsync<int>(sql, {| callbackId = callbackId |})
        return count > 0
    }

type MlEnabledVahterTestContainers() =
    inherit VahterTestContainers(mlEnabled = true)

type MlDisabledVahterTestContainers() =
    inherit VahterTestContainers(mlEnabled = false)

// workaround to wait for ML to be ready
type MlAwaitFixture() =
    interface IAsyncLifetime with
        // we assume 5 seconds is enough for model to train. Could be flaky
        member this.InitializeAsync() = ValueTask(Task.Delay 10000)
    interface IAsyncDisposable with
        member this.DisposeAsync() = ValueTask()
