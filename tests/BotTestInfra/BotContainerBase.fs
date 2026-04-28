namespace BotTestInfra

open System
open System.IO
open System.Net.Http
open System.Net.Http.Json
open System.Text
open System.Text.Json
open System.Threading.Tasks
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Configurations
open DotNet.Testcontainers.Containers
open DotNet.Testcontainers.Images
open DotNet.Testcontainers.Networks
open Testcontainers.PostgreSql
open BotTestInfra.ContainerHelpers
open Xunit

/// Configuration for a bot's test container setup.
type BotContainerConfig =
    { BotProject: string
      MigrationsSubdir: string
      DbName: string
      DbUser: string
      DbPassword: string
      AppImageName: string
      OcrEnabled: bool
      SecretToken: string
      WebhookRoute: string
      /// Extra environment variables for the app container.
      AppEnvVars: (string * string) list }

/// Shared container lifecycle for bot integration tests.
/// Orchestrates: network, postgres, init.sql, flyway, fake TG API, fake Azure OCR, bot app.
/// Subclasses provide bot-specific DB seeding and domain helpers.
[<AbstractClass>]
type BotContainerBase(config: BotContainerConfig) =
    let solutionDir = CommonDirectoryPath.GetSolutionDirectory()
    let solutionDirPath = solutionDir.DirectoryPath
    let dbAlias = config.MigrationsSubdir + "-db"
    let fakeAlias = "fake-tg-api"
    let fakeAzureAlias = "fake-azure-ocr"
    let pgImage = "postgres:15.6"

    let internalConnectionString =
        $"Server={dbAlias};Database={config.DbName};Port=5432;User Id={config.DbUser};Password={config.DbPassword};Include Error Detail=true;Minimum Pool Size=1;Maximum Pool Size=20;Max Auto Prepare=100;Auto Prepare Min Usages=1;Trust Server Certificate=true;"

    let mutable botHttp: HttpClient = null
    let mutable fakeTgHttp: HttpClient = null
    let mutable fakeAzureHttp: HttpClient = null
    let mutable publicConnectionString: string = null
    let mutable testArtifactsDir: string = null

    let network = createNetwork()
    let dbContainer = createPostgresContainer network dbAlias pgImage
    let migrationsPath = Path.Combine(solutionDirPath, "src", config.MigrationsSubdir, "migrations")
    let flywayContainer = createFlywayContainer network migrationsPath dbAlias config.DbName dbContainer

    let fakeTgImage, fakeTgBuildLogger =
        buildImageSpec solutionDir "./tests/Dockerfile.fake" $"{config.AppImageName}-fake-tg-api" true true ["FAKE_PROJECT", "FakeTgApi"; "FAKE_PORT", "8080"]
    let fakeTgContainer = createFakeTgApiContainer fakeTgImage network fakeAlias

    let fakeAzureImage, fakeAzureBuildLogger =
        buildImageSpec solutionDir "./tests/Dockerfile.fake" $"{config.AppImageName}-fake-azure-ocr" true true ["FAKE_PROJECT", "FakeAzureOcrApi"; "FAKE_PORT", "8081"]
    let fakeAzureContainer = createFakeAzureOcrContainer fakeAzureImage network fakeAzureAlias

    let botImage, botBuildLogger =
        let logger = StringLogger()
        let img =
            ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(solutionDir, String.Empty)
                .WithDockerfile("./src/Dockerfile.bot")
                .WithName(config.AppImageName)
                .WithBuildArgument("BOT_PROJECT", config.BotProject)
                .WithBuildArgument("RESOURCE_REAPER_SESSION_ID", ResourceReaper.DefaultSessionId.ToString("D"))
                .WithDeleteIfExists(true)
                .WithCleanUp(true)
                .WithLogger(logger)
                .Build()
        (img, logger)

    let botContainer =
        let mutable b =
            ContainerBuilder(botImage)
                .WithNetwork(network)
                .WithPortBinding(80, true)
                .WithEnvironment("DATABASE_URL", internalConnectionString)
                .WithEnvironment("ASPNETCORE_HTTP_PORTS", "80")
                .DependsOn(flywayContainer)
                .DependsOn(fakeTgContainer)
        for (key, value) in config.AppEnvVars do
            b <- b.WithEnvironment(key, value)
        if config.OcrEnabled then
            b <- b.DependsOn(fakeAzureContainer)
        b.WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(80))
            .Build()

    /// Override to seed the database after migrations run and before the app starts.
    abstract SeedDatabase: connString: string -> Task
    default _.SeedDatabase(_) = Task.CompletedTask

    /// Override to run additional setup after the bot container is started and HTTP clients are ready.
    /// Intended for readiness-probe polling and post-startup data extraction.
    abstract AfterStart: unit -> Task
    default _.AfterStart() = Task.CompletedTask

    interface IAsyncLifetime with
        member this.InitializeAsync() =
            ValueTask(task {
                testArtifactsDir <- Path.Combine(solutionDirPath, "test-artifacts", $"{config.BotProject}.Tests", this.GetType().Name)
                do! dbContainer.StartAsync()

                publicConnectionString <-
                    $"Server=127.0.0.1;Database={config.DbName};Port={dbContainer.GetMappedPublicPort(5432)};User Id={config.DbUser};Password={config.DbPassword};Include Error Detail=true;Timeout=120;Command Timeout=120;Keepalive=30;"

                // init schema/user/db
                let initSql = File.ReadAllText(Path.Combine(solutionDirPath, "src", config.MigrationsSubdir, "init.sql"))
                let! initResult = dbContainer.ExecScriptAsync(initSql)
                if initResult.Stderr <> "" then failwith initResult.Stderr

                // run migrations
                do! flywayContainer.StartAsync()

                // seed database (subclass hook)
                do! this.SeedDatabase(publicConnectionString)

                // build images in parallel
                let botBuildTask = buildImageWithLogs testArtifactsDir "bot" botImage botBuildLogger
                let fakeTgBuildTask = buildImageWithLogs testArtifactsDir "fake-tg-api" fakeTgImage fakeTgBuildLogger
                let fakeAzureBuildTask =
                    if config.OcrEnabled then buildImageWithLogs testArtifactsDir "fake-azure-ocr" fakeAzureImage fakeAzureBuildLogger
                    else Task.CompletedTask
                do! Task.WhenAll([| botBuildTask; fakeTgBuildTask; fakeAzureBuildTask |])

                do! fakeTgContainer.StartAsync()
                if config.OcrEnabled then
                    do! fakeAzureContainer.StartAsync()
                do! botContainer.StartAsync()

                botHttp <- new HttpClient(BaseAddress = Uri($"http://127.0.0.1:{botContainer.GetMappedPublicPort(80)}"))
                botHttp.Timeout <- TimeSpan.FromSeconds(15.0)
                botHttp.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", config.SecretToken)

                fakeTgHttp <- new HttpClient(BaseAddress = Uri($"http://127.0.0.1:{fakeTgContainer.GetMappedPublicPort(8080)}"))
                fakeTgHttp.Timeout <- TimeSpan.FromSeconds(5.0)

                if config.OcrEnabled then
                    fakeAzureHttp <- new HttpClient(BaseAddress = Uri($"http://127.0.0.1:{fakeAzureContainer.GetMappedPublicPort(8081)}"))
                    fakeAzureHttp.Timeout <- TimeSpan.FromSeconds(5.0)

                do! this.AfterStart()
            } :> Task)

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                let! _ = dumpContainerLogs testArtifactsDir "bot" botContainer
                let! _ = dumpContainerLogs testArtifactsDir "fake-tg-api" fakeTgContainer
                if config.OcrEnabled then
                    let! _ = dumpContainerLogs testArtifactsDir "fake-azure-ocr" fakeAzureContainer
                    ()
                let! _ = dumpContainerLogs testArtifactsDir "flyway" flywayContainer
                let! _ = dumpContainerLogs testArtifactsDir "postgres" dbContainer

                if not (isNull botHttp) then botHttp.Dispose()
                if not (isNull fakeTgHttp) then fakeTgHttp.Dispose()
                if not (isNull fakeAzureHttp) then fakeAzureHttp.Dispose()
                do! botContainer.DisposeAsync()
                do! fakeTgContainer.DisposeAsync()
                if config.OcrEnabled then
                    do! fakeAzureContainer.DisposeAsync()
                do! flywayContainer.DisposeAsync()
                do! dbContainer.DisposeAsync()
            } :> Task)

    // ── Exposed clients ─────────────────────────────────────────────────
    member _.BotHttp = botHttp
    member _.FakeTgHttp = fakeTgHttp
    member _.FakeAzureHttp = fakeAzureHttp
    member _.DbConnectionString = publicConnectionString
    member _.OcrEnabled = config.OcrEnabled

    // ── Shared helpers ──────────────────────────────────────────────────

    member _.GetBotLogs() =
        task {
            let! (stdout, stderr) = botContainer.GetLogsAsync()
            return $"=== STDOUT ===\n{stdout}\n=== STDERR ===\n{stderr}"
        }

    member _.GetAllLogs() =
        task {
            let sb = StringBuilder()
            for (name, container: IContainer) in
                [ "bot", botContainer
                  "fake-tg-api", fakeTgContainer
                  "postgres", dbContainer ] do
                let! (stdout, stderr) = container.GetLogsAsync()
                sb.AppendLine($"=== {name} STDOUT ===").AppendLine(stdout) |> ignore
                sb.AppendLine($"=== {name} STDERR ===").AppendLine(stderr) |> ignore
            if config.OcrEnabled then
                let! (stdout, stderr) = fakeAzureContainer.GetLogsAsync()
                sb.AppendLine("=== fake-azure-ocr STDOUT ===").AppendLine(stdout) |> ignore
                sb.AppendLine("=== fake-azure-ocr STDERR ===").AppendLine(stderr) |> ignore
            return sb.ToString()
        }

    member _.SendUpdate(update: Telegram.Bot.Types.Update) =
        task {
            let jsonOptions = JsonSerializerOptions(JsonSerializerDefaults.Web)
            Telegram.Bot.JsonBotAPI.Configure(jsonOptions)
            let json = JsonSerializer.Serialize(update, jsonOptions)
            use content = new StringContent(json, Encoding.UTF8, "application/json")
            return! botHttp.PostAsync(config.WebhookRoute, content)
        }

    member _.ClearFakeCalls() =
        task {
            let! _ = fakeTgHttp.DeleteAsync("/test/calls")
            return ()
        }

    member _.GetFakeCalls(method: string) =
        task {
            let! resp = fakeTgHttp.GetFromJsonAsync<FakeCall array>($"/test/calls?method={method}")
            return resp
        }

    member _.SetChatMemberStatus(userId: int64, status: string) =
        task {
            let payload: ChatMemberMock = { userId = userId; status = status }
            let! _ = fakeTgHttp.PostAsJsonAsync("/test/mock/chatMember", payload)
            return ()
        }

    member _.SetTelegramFile(fileId: string, bytes: byte[]) =
        task {
            let payload: FileMock =
                { fileId = fileId
                  contentBase64 = Convert.ToBase64String(bytes) }
            let! _ = fakeTgHttp.PostAsJsonAsync("/test/mock/file", payload)
            return ()
        }

    member _.SetMethodError(methodName: string, enabled: bool) =
        task {
            let payload: MethodErrorMock = { methodName = methodName; enabled = enabled }
            let! resp = fakeTgHttp.PostAsJsonAsync("/test/mock/methodError", payload)
            resp.EnsureSuccessStatusCode() |> ignore
        }

    member _.CheckMethodErrorActive(methodName: string) =
        task {
            use content = new StringContent("{}", Encoding.UTF8, "application/json")
            let! resp = fakeTgHttp.PostAsync($"/bottest/{methodName}", content)
            return resp.StatusCode
        }

    member _.SetAzureOcrResponse(status: int, body: string) =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let payload: AzureResponseMock = { status = status; body = body }
            let! _ = fakeAzureHttp.PostAsJsonAsync("/test/mock/response", payload)
            return ()
        }
