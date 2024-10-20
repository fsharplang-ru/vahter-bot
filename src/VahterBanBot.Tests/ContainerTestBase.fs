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
open Funogram.Telegram.Types
open Npgsql
open Testcontainers.PostgreSql
open VahterBanBot.Tests.TgMessageUtils
open VahterBanBot.Types
open VahterBanBot.Utils
open Xunit
open Dapper

type VahterTestContainers() =
    let solutionDir = CommonDirectoryPath.GetSolutionDirectory()
    let dbAlias = "vahter-db"
    let internalConnectionString = $"Server={dbAlias};Database=vahter_bot_ban;Port=5432;User Id=vahter_bot_ban_service;Password=vahter_bot_ban_service;Include Error Detail=true;Minimum Pool Size=1;Maximum Pool Size=20;Max Auto Prepare=100;Auto Prepare Min Usages=1;Trust Server Certificate=true;"
    let pgImage = "postgres:15.6" // same as in Azure
    
    // will be filled in IAsyncLifetime.InitializeAsync
    let mutable uri: Uri = null
    let mutable httpClient: HttpClient = null
    let mutable publicConnectionString: string = null
    
    // base image for the app, we'll build exactly how we build it in Azure
    let image =
        ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionDir, String.Empty)
            .WithDockerfile("./Dockerfile")
            .WithName("vahter-bot-ban-test")
            // workaround for multi-stage builds cleanup
            .WithBuildArgument("RESOURCE_REAPER_SESSION_ID", ResourceReaper.DefaultSessionId.ToString("D"))
            // it might speed up the process to not clean up the base image
            .WithCleanUp(false)
            .Build()

    // private network for the containers
    let network =
        NetworkBuilder()
            .Build()

    // PostgreSQL container. Important to have the same image as in Azure
    // and assign network alias to it as it will be "host" in DB connection string for the app
    let dbContainer =
        PostgreSqlBuilder()
            .WithImage(pgImage) 
            .WithNetwork(network)
            .WithNetworkAliases(dbAlias)
            .Build()

    // Flyway container to run migrations
    let flywayContainer =
        ContainerBuilder()
            .WithImage("redgate/flyway")
            .WithNetwork(network)
            .WithBindMount(CommonDirectoryPath.GetSolutionDirectory().DirectoryPath + "/src/migrations", "/flyway/sql", AccessMode.ReadOnly)
            .WithEnvironment("FLYWAY_URL", "jdbc:postgresql://vahter-db:5432/vahter_bot_ban")
            .WithEnvironment("FLYWAY_USER", "vahter_bot_ban_service")
            .WithEnvironment("FLYWAY_PASSWORD", "vahter_bot_ban_service")
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
    // we'll pass all the necessary environment variables to it
    let appContainer =
        ContainerBuilder()
            .WithImage(image)
            .WithNetwork(network)
            .WithPortBinding(80, true)
            .WithEnvironment("BOT_USER_ID", "1337")
            .WithEnvironment("BOT_USER_NAME", "test_bot")
            .WithEnvironment("BOT_TELEGRAM_TOKEN", "123:456")
            .WithEnvironment("BOT_AUTH_TOKEN", "OUR_SECRET")
            .WithEnvironment("LOGS_CHANNEL_ID", "-123")
            .WithEnvironment("CHATS_TO_MONITOR", """{"pro.hell":"-666","dotnetru":-42}""")
            .WithEnvironment("ALLOWED_USERS", """{"vahter_1":"34","vahter_2":69}""")
            .WithEnvironment("SHOULD_DELETE_CHANNEL_MESSAGES", "true")
            .WithEnvironment("IGNORE_SIDE_EFFECTS", "false")
            .WithEnvironment("USE_FAKE_TG_API", "true")
            .WithEnvironment("USE_POLLING", "false")
            .WithEnvironment("DATABASE_URL", internalConnectionString)
            .WithEnvironment("CLEANUP_OLD_MESSAGES", "false")
            .WithEnvironment("ML_ENABLED", "true")
            .WithEnvironment("ML_SEED", "42")
            .WithEnvironment("ML_TRAIN_RANDOM_SORT_DATA", "false")
            .WithEnvironment("ML_SPAM_DELETION_ENABLED", "true")
            .WithEnvironment("ML_SPAM_THRESHOLD", "1.0")
            .WithEnvironment("ML_STOP_WORDS_IN_CHATS", """{"-42":["2"]}""")
            .WithEnvironment("ML_SPAM_AUTOBAN_ENABLED", "true")
            .WithEnvironment("ML_SPAM_AUTOBAN_CHECK_LAST_MSG_COUNT", "10")
            .WithEnvironment("ML_SPAM_AUTOBAN_SCORE_THRESHOLD", "-4.0")
            // .net 8.0 upgrade has a breaking change
            // https://learn.microsoft.com/en-us/dotnet/core/compatibility/containers/8.0/aspnet-port
            // Azure default port for containers is 80, se we need explicitly set it
            .WithEnvironment("ASPNETCORE_HTTP_PORTS", "80")
            .WithEnvironment("UPDATE_CHAT_ADMINS", "true")
            .WithEnvironment("UPDATE_CHAT_ADMINS_INTERVAL_SEC", "86400")
            .DependsOn(flywayContainer)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(80))
            .Build()

    interface IAsyncLifetime with
        member this.InitializeAsync() = task {
            try
                // start building the image and spin up db at the same time
                let imageTask = image.CreateAsync()
                let dbTask = dbContainer.StartAsync()

                // wait for both to finish
                do! imageTask
                do! dbTask
                publicConnectionString <- $"Server=127.0.0.1;Database=vahter_bot_ban;Port={dbContainer.GetMappedPublicPort(5432)};User Id=vahter_bot_ban_service;Password=vahter_bot_ban_service;Include Error Detail=true;Minimum Pool Size=1;Maximum Pool Size=20;Max Auto Prepare=100;Auto Prepare Min Usages=1;Trust Server Certificate=true;"
                
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
                do! dbContainer.CopyAsync(Encoding.Default.GetBytes script, scriptFilePath, Unix.FileMode644)
                let! scriptResult = dbContainer.ExecAsync [|"psql"; "--username"; "vahter_bot_ban_service"; "--dbname"; "vahter_bot_ban"; "--file"; scriptFilePath |]

                if scriptResult.Stderr <> "" then
                    failwith scriptResult.Stderr

                // start the app container
                do! appContainer.StartAsync()
                
                // initialize the http client with correct hostname and port
                httpClient <- new HttpClient()
                uri <- Uri($"http://{appContainer.Hostname}:{appContainer.GetMappedPublicPort(80)}")
                httpClient.BaseAddress <- uri
                httpClient.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", "OUR_SECRET")
            finally
                let struct (_, err) = appContainer.GetLogsAsync().Result
                if err <> "" then
                    failwith err
        }
        member this.DisposeAsync() = task {
            // stop all the containers, flyway might be dead already
            do! flywayContainer.DisposeAsync()
            do! appContainer.DisposeAsync()
            do! dbContainer.DisposeAsync()
            // do! image.DisposeAsync() // might be faster not to dispose base image to cache?
        }

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

    member _.LogChat = Tg.chat(id = -123, username = "logs")
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
        use conn = new NpgsqlConnection(publicConnectionString)
        //language=postgresql
        let sql = """
SELECT COUNT(*) FROM false_positive_messages
WHERE text = @text
"""
        let! result = conn.QuerySingleAsync<int>(sql, {| text = msg.Text |})
        return result > 0
    }

// workaround to wait for ML to be ready
type MlAwaitFixture() =
    interface IAsyncLifetime with
        member this.DisposeAsync() = Task.CompletedTask
        // we assume 5 seconds is enough for model to train. Could be flaky
        member this.InitializeAsync() = Task.Delay 10000
