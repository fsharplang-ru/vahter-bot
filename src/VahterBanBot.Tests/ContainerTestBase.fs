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
                    "CLEANUP_OLD_MESSAGES",      "false",                                   "FEATURE_FLAG", "CLEANUP"
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

    member _.TryGetDbMessage(msg: Message) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
        //language=postgresql
        let sql = "SELECT * FROM message WHERE chat_id = @chatId AND message_id = @messageId"
        let! dbMessage = conn.QueryAsync<DbMessage>(sql, {| chatId = msg.Chat.Id; messageId = msg.MessageId |})
        return dbMessage |> Seq.tryHead
    }

    member _.MessageBanned(msg: Message) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
        //language=postgresql
        let sql = "SELECT COUNT(*) FROM banned WHERE banned_in_chat_id = @chatId AND message_id = @messageId"
        let! count = conn.QuerySingleAsync<int>(sql, {| chatId = msg.Chat.Id; messageId = msg.MessageId |})
        return count > 0
    }
    
    member _.UserBanned(userId: int64) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
        //language=postgresql
        let sql = "SELECT COUNT(*) FROM banned WHERE banned_user_id = @userId"
        let! count = conn.QuerySingleAsync<int>(sql, {| userId = userId |})
        return count > 0
    }
    
    member _.MessageIsAutoDeleted(msg: Message) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
        //language=postgresql
        let sql = "SELECT COUNT(*) FROM banned_by_bot WHERE banned_in_chat_id = @chatId AND message_id = @messageId"
        let! count = conn.QuerySingleAsync<int>(sql, {| chatId = msg.Chat.Id; messageId = msg.MessageId |})
        return count > 0
    }
    
    member _.GetCallbackId(msg: Message) (caseName: string) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
        //language=postgresql
        let sql = """
SELECT id
FROM callback
WHERE data ->> 'Case' = @caseName
  AND data -> 'Fields' -> 0 -> 'message' ->> 'message_id' = @messageId::TEXT
  AND data -> 'Fields' -> 0 -> 'message' -> 'chat' ->> 'id' = @chatId::TEXT
"""
        return! conn.QuerySingleAsync<Guid>(
            sql, {| chatId = msg.Chat.Id
                    messageId = msg.MessageId
                    caseName = caseName |})
    }
    
    member _.IsMessageFalsePositive(msg: Message) = task {
        if String.IsNullOrWhiteSpace(msg.Text) then return false else
        
        use conn = new NpgsqlConnection(publicConnectionString)
        //language=postgresql
        let sql = """
            SELECT EXISTS (
                SELECT 1 
                FROM false_positive_messages 
                WHERE text_hash = md5(@text)::uuid 
                  AND text = @text
            )
        """
        return! conn.QuerySingleAsync<bool>(sql, {| text = msg.Text |})
    }

    member _.UserBannedByBot(userId: int64) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
        //language=postgresql
        let sql = "SELECT COUNT(*) FROM banned_by_bot WHERE banned_user_id = @userId"
        let! count = conn.QuerySingleAsync<int>(sql, {| userId = userId |})
        return count > 0
    }
    
    member _.GetUserReactionCount(userId: int64) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
        //language=postgresql
        let sql = """SELECT reaction_count FROM "user" WHERE id = @userId"""
        let! result = conn.QueryAsync<int>(sql, {| userId = userId |})
        return result |> Seq.tryHead |> Option.defaultValue 0
    }
    
    member _.IsMessageFalseNegative(msg: Message) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
        //language=postgresql
        let sql = "SELECT COUNT(*) FROM false_negative_messages WHERE chat_id = @chatId AND message_id = @messageId"
        let! count = conn.QuerySingleAsync<int>(sql, {| chatId = msg.Chat.Id; messageId = msg.MessageId |})
        return count > 0
    }
    
    /// Checks if message was deleted (exists in fake API deleted list)
    member _.MessageWasDeleted(msg: Message) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
        // Check if message is in banned_by_bot (for auto-deleted) or was manually deleted
        // For soft spam, we check the fake API's deleted messages
        //language=postgresql
        let sql = "SELECT COUNT(*) FROM banned_by_bot WHERE banned_in_chat_id = @chatId AND message_id = @messageId"
        let! count = conn.QuerySingleAsync<int>(sql, {| chatId = msg.Chat.Id; messageId = msg.MessageId |})
        return count > 0
    }

    member _.TryGetLlmTriageVerdict(msg: Message) = task {
        use conn = new NpgsqlConnection(publicConnectionString)
        //language=postgresql
        let sql = "SELECT verdict FROM llm_triage WHERE chat_id = @chatId AND message_id = @messageId"
        let! verdicts = conn.QueryAsync<string>(sql, {| chatId = msg.Chat.Id; messageId = msg.MessageId |})
        return verdicts |> Seq.tryHead
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
