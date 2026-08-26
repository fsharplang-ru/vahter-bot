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
      AppEnvVars: (string * string) list
      /// Postgres image tag for the test container. Additive (Slice 5a: AlitaBot needs
      /// pgvector for its message_embedding table) — every other bot keeps "postgres:17.10"
      /// so this is scoped to Alita alone.
      PostgresImage: string }

/// Shared container lifecycle for bot integration tests.
/// Orchestrates: network, postgres, init.sql, flyway, fake TG API, fake Azure OCR, N app
/// containers from ONE cached image spec (ContainerHelpers.getOrCreateImageSpec /
/// buildImageOncePerProcess — a per-instance rebuild of the same tag races and 409s on podman).
/// Subclasses provide bot-specific DB seeding and domain helpers.
///
/// COMPAT: `instanceCount` defaults to 1. Every pre-existing public singular member (BotHttp,
/// SendUpdate, RestartBotApp, GetBotLogs(), the "bot"-named log dump, etc.) delegates to
/// instance 0 with byte-identical behavior, so the 10 pre-existing single-pod fixtures need
/// zero source changes. N>1 additionally exposes a plural surface — BotHttpAt(i),
/// SendUpdateTo(i, update), GetBotLogs(i) (dumped as "bot-i" logs), GetSettingsDump(i),
/// AdvanceAllClocks(ms) — for multi-pod fixtures (see MultiPodContainerBase).
[<AbstractClass>]
type BotContainerBase(config: BotContainerConfig, ?instanceCount: int) =
    let n = defaultArg instanceCount 1
    let solutionDir = CommonDirectoryPath.GetSolutionDirectory()
    let solutionDirPath = solutionDir.DirectoryPath
    let dbAlias = config.MigrationsSubdir + "-db"
    let fakeAlias = "fake-tg-api"
    let fakeAzureAlias = "fake-azure-ocr"
    let pgImage = config.PostgresImage

    let internalConnectionString =
        $"Server={dbAlias};Database={config.DbName};Port=5432;User Id={config.DbUser};Password={config.DbPassword};Include Error Detail=true;Minimum Pool Size=1;Maximum Pool Size=20;Max Auto Prepare=100;Auto Prepare Min Usages=1;Trust Server Certificate=true;"

    let mutable botHttps: HttpClient[] = [||]
    let mutable fakeTgHttp: HttpClient = null
    let mutable fakeAzureHttp: HttpClient = null
    let mutable publicConnectionString: string = null
    let mutable adminConnectionString: string = null
    let mutable testArtifactsDir: string = null

    let network = createNetwork()
    let dbContainer = createPostgresContainer network dbAlias pgImage
    let migrationsPath = Path.Combine(solutionDirPath, "src", config.MigrationsSubdir, "migrations")
    let flywayContainer = createFlywayContainer network migrationsPath dbAlias config.DbName dbContainer

    let fakeTgImage, fakeTgBuildLogger =
        getOrCreateImageSpec $"{config.AppImageName}-fake-tg-api" (fun () ->
            buildImageSpec solutionDir "./tests/Dockerfile.fake" $"{config.AppImageName}-fake-tg-api" true true ["FAKE_PROJECT", "FakeTgApi"; "FAKE_PORT", "8080"])
    let fakeTgContainer = createFakeTgApiContainer fakeTgImage network fakeAlias

    let fakeAzureImage, fakeAzureBuildLogger =
        getOrCreateImageSpec $"{config.AppImageName}-fake-azure-ocr" (fun () ->
            buildImageSpec solutionDir "./tests/Dockerfile.fake" $"{config.AppImageName}-fake-azure-ocr" true true ["FAKE_PROJECT", "FakeAzureOcrApi"; "FAKE_PORT", "8081"])
    let fakeAzureContainer = createFakeAzureOcrContainer fakeAzureImage network fakeAzureAlias

    let botImage, botBuildLogger =
        getOrCreateImageSpec config.AppImageName (fun () ->
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
            (img, logger))

    let makeBotContainer () =
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

    // N app containers built from the SAME cached image spec above — never a per-instance
    // rebuild (Array.init only creates N *container* handles, not N image builds).
    let botContainers: IContainer[] = Array.init n (fun _ -> makeBotContainer())

    /// Override to seed the database after migrations run and before any instance starts.
    abstract SeedDatabase: connString: string -> Task
    default _.SeedDatabase(_) = Task.CompletedTask

    /// Override to run additional setup after the bot container(s) are started and HTTP
    /// clients are ready. Intended for readiness-probe polling and post-startup data extraction.
    abstract AfterStart: unit -> Task
    default _.AfterStart() = Task.CompletedTask

    interface IAsyncLifetime with
        member this.InitializeAsync() =
            ValueTask(task {
                testArtifactsDir <- Path.Combine(solutionDirPath, "test-artifacts", $"{config.BotProject}.Tests", this.GetType().Name)
                do! dbContainer.StartAsync()

                let mappedPort = dbContainer.GetMappedPublicPort(5432)
                let connStr (user: string) (password: string) =
                    $"Server=127.0.0.1;Database={config.DbName};Port={mappedPort};User Id={user};Password={password};Include Error Detail=true;Timeout=120;Command Timeout=120;Keepalive=30;"
                publicConnectionString <- connStr config.DbUser config.DbPassword
                // `admin` owns the DB (see init.sql) and runs migrations (FLYWAY_USER) — use it for
                // owner-only ops like TRUNCATE that the least-privilege bot role intentionally lacks.
                adminConnectionString <- connStr "admin" "admin"

                // init schema/user/db
                let initSql = File.ReadAllText(Path.Combine(solutionDirPath, "src", config.MigrationsSubdir, "init.sql"))
                let! initResult = dbContainer.ExecScriptAsync(initSql)
                if initResult.Stderr <> "" then failwith initResult.Stderr

                // run migrations
                do! flywayContainer.StartAsync()
                // The wait strategy only waits for flyway to exit; without this check a failed
                // migration run only surfaces later as a cryptic "relation does not exist"
                // during seeding, with the actual cause hidden in the flyway container log.
                let! flywayExitCode = flywayContainer.GetExitCodeAsync()
                if flywayExitCode <> 0L then
                    let! struct (stdout, stderr) = flywayContainer.GetLogsAsync()
                    failwith $"Flyway migrations failed (exit code {flywayExitCode})\n=== STDOUT ===\n{stdout}\n=== STDERR ===\n{stderr}"

                // seed database (subclass hook) — runs exactly once, before any instance starts.
                do! this.SeedDatabase(publicConnectionString)

                // build images in parallel (each image name at most once per process — the
                // parallel-initializing fixtures share the same image names)
                let botBuildTask = buildImageOncePerProcess config.AppImageName testArtifactsDir "bot" botImage botBuildLogger
                let fakeTgBuildTask = buildImageOncePerProcess $"{config.AppImageName}-fake-tg-api" testArtifactsDir "fake-tg-api" fakeTgImage fakeTgBuildLogger
                let fakeAzureBuildTask =
                    if config.OcrEnabled then buildImageOncePerProcess $"{config.AppImageName}-fake-azure-ocr" testArtifactsDir "fake-azure-ocr" fakeAzureImage fakeAzureBuildLogger
                    else Task.CompletedTask
                do! Task.WhenAll([| botBuildTask; fakeTgBuildTask; fakeAzureBuildTask |])

                do! fakeTgContainer.StartAsync()
                if config.OcrEnabled then
                    do! fakeAzureContainer.StartAsync()

                // Start every instance in parallel from the one already-built image.
                do! Task.WhenAll(botContainers |> Array.map (fun c -> c.StartAsync()))

                botHttps <-
                    botContainers
                    |> Array.map (fun c ->
                        let http = new HttpClient(BaseAddress = Uri($"http://127.0.0.1:{c.GetMappedPublicPort(80)}"))
                        http.Timeout <- TimeSpan.FromSeconds(15.0)
                        http.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", config.SecretToken)
                        http)

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
                // N=1 keeps the legacy "bot" log dump name byte-identical; N>1 uses "bot-i".
                for i in 0 .. n - 1 do
                    let logName = if n = 1 then "bot" else $"bot-{i}"
                    let! _ = dumpContainerLogs testArtifactsDir logName botContainers[i]
                    ()
                let! _ = dumpContainerLogs testArtifactsDir "fake-tg-api" fakeTgContainer
                if config.OcrEnabled then
                    let! _ = dumpContainerLogs testArtifactsDir "fake-azure-ocr" fakeAzureContainer
                    ()
                let! _ = dumpContainerLogs testArtifactsDir "flyway" flywayContainer
                let! _ = dumpContainerLogs testArtifactsDir "postgres" dbContainer

                for http in botHttps do
                    if not (isNull http) then http.Dispose()
                if not (isNull fakeTgHttp) then fakeTgHttp.Dispose()
                if not (isNull fakeAzureHttp) then fakeAzureHttp.Dispose()
                do! Task.WhenAll(botContainers |> Array.map (fun c -> c.DisposeAsync().AsTask()))
                do! fakeTgContainer.DisposeAsync()
                if config.OcrEnabled then
                    do! fakeAzureContainer.DisposeAsync()
                do! flywayContainer.DisposeAsync()
                do! dbContainer.DisposeAsync()
            } :> Task)

    // ── Exposed clients ─────────────────────────────────────────────────
    /// Instance 0's HTTP client — unchanged accessor for every single-pod fixture.
    member _.BotHttp = botHttps[0]
    member _.FakeTgHttp = fakeTgHttp
    member _.FakeAzureHttp = fakeAzureHttp
    /// Connection string as the bot's least-privilege service role (`config.DbUser`).
    member _.DbConnectionString = publicConnectionString
    /// Connection string as the table owner `admin` — for owner-only ops (DDL/TRUNCATE) the
    /// service role intentionally can't perform.
    member _.AdminDbConnectionString = adminConnectionString
    member _.OcrEnabled = config.OcrEnabled
    /// Number of app instances (1 for every pre-existing single-pod fixture).
    member _.InstanceCount = n
    /// Instance `i`'s HTTP client — plural surface for N>1 fixtures. Named `BotHttpAt` (not an
    /// overload of `BotHttp`) because F# resolves a same-named property + method pair as one
    /// ambiguous overload group, breaking `BotHttp`'s no-args property semantics.
    member _.BotHttpAt(i: int) = botHttps[i]

    // ── Shared helpers ──────────────────────────────────────────────────

    member _.GetBotLogs() =
        task {
            let! (stdout, stderr) = botContainers[0].GetLogsAsync()
            return $"=== STDOUT ===\n{stdout}\n=== STDERR ===\n{stderr}"
        }

    /// Instance `i`'s container logs — plural surface for N>1 fixtures.
    member _.GetBotLogs(i: int) =
        task {
            let! (stdout, stderr) = botContainers[i].GetLogsAsync()
            return $"=== STDOUT ===\n{stdout}\n=== STDERR ===\n{stderr}"
        }

    member _.GetAllLogs() =
        task {
            let sb = StringBuilder()
            for (name, container: IContainer) in
                [ "bot", botContainers[0]
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

    member _.SendUpdate(update: Funogram.Telegram.Types.Update) =
        task {
            let json = Encoding.UTF8.GetString(Funogram.Tools.toJson update)
            use content = new StringContent(json, Encoding.UTF8, "application/json")
            return! botHttps[0].PostAsync(config.WebhookRoute, content)
        }

    /// Sends an update to instance `i`'s webhook route — plural surface for N>1 fixtures.
    /// FakeTgApi is shared across all instances (FakeCall has no instance-identity field) —
    /// assert on aggregate call content.
    member _.SendUpdateTo(i: int, update: Funogram.Telegram.Types.Update) =
        task {
            let json = Encoding.UTF8.GetString(Funogram.Tools.toJson update)
            use content = new StringContent(json, Encoding.UTF8, "application/json")
            return! botHttps[i].PostAsync(config.WebhookRoute, content)
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

    /// Registers a username->chat mapping so the bot's getChat("@username") resolves
    /// to the given id/title (used by /vahter addchat @username tests).
    member _.SetMockChat(username: string, id: int64, title: string) =
        task {
            let payload: ChatMock = { username = username; id = id; title = title }
            let! _ = fakeTgHttp.PostAsJsonAsync("/test/mock/chat", payload)
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

    /// Forces FakeTgApi to artificially delay every call to `methodName` by
    /// `delayMs` milliseconds. Used by concurrency-race tests to deterministically
    /// reproduce timing-dependent bugs: by widening the window between a
    /// transaction commit and the next network call, a second concurrent
    /// webhook can be made to win the lock-acquisition race every single run.
    /// Pass `delayMs = 0` to clear.
    member _.SetFakeTgMethodDelay(methodName: string, delayMs: int) =
        task {
            let payload: MethodDelayMock = { methodName = methodName; delayMs = delayMs }
            let! resp = fakeTgHttp.PostAsJsonAsync("/test/mock/methodDelay", payload)
            resp.EnsureSuccessStatusCode() |> ignore
        }

    /// AlitaBot Slice 6: when `enabled`, FakeTgApi rejects (simulated 400) any
    /// sendMessage/editMessageText call carrying MarkdownV2 formatting — used to exercise
    /// `Mdv2Delivery`'s plain-text fallback. `fixture`'s `ClearFakeCalls`/test teardown
    /// doesn't reset this by itself except via FakeTgApi's own `/test/calls` DELETE
    /// (`Store.clearCalls`), which does — callers should still explicitly reset to false
    /// when done, mirroring `SetMethodError`'s convention.
    member _.SetMdv2Rejected(enabled: bool) =
        task {
            let payload: RejectMdv2Mock = { enabled = enabled }
            let! resp = fakeTgHttp.PostAsJsonAsync("/test/mock/rejectMdv2", payload)
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

    /// Resets the fake OCR mock to its pristine baseline (default 200 response, no delay,
    /// no error mode, empty script). The fake is shared across the whole test assembly, so
    /// tests must reset it rather than inherit a previous test's custom response/error mode.
    member _.ResetAzureOcr() =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            use content = new StringContent("{}", Encoding.UTF8, "application/json")
            let! _ = fakeAzureHttp.PostAsync("/test/mock/reset", content)
            return ()
        }

    member _.SetAzureOcrDelay(delayMs: int) =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let payload: AzureDelayMock = { delayMs = delayMs }
            let! _ = fakeAzureHttp.PostAsJsonAsync("/test/mock/delay", payload)
            return ()
        }

    member _.SetAzureOcrErrorMode(mode: string) =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let payload: AzureErrorModeMock = { mode = mode }
            let! _ = fakeAzureHttp.PostAsJsonAsync("/test/mock/errorMode", payload)
            return ()
        }

    member _.SetAzureOcrScript(responses: AzureScriptedResponse array) =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let payload: AzureScriptMock = { responses = responses }
            let! _ = fakeAzureHttp.PostAsJsonAsync("/test/mock/script", payload)
            return ()
        }

    member _.ClearAzureOcrCalls() =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let! _ = fakeAzureHttp.DeleteAsync("/test/calls")
            return ()
        }

    member _.GetAzureOcrCalls() =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let! resp = fakeAzureHttp.GetFromJsonAsync<FakeCall array>("/test/calls")
            return resp
        }

    /// Scripts the Azure OpenAI chat-completions endpoint (e.g. a 429 then fall-through to a
    /// keyword-routed 200) so tests can exercise the bot's retry/backoff and failure handling.
    /// An empty array clears the script.
    member _.SetAzureLlmScript(responses: AzureScriptedResponse array) =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let payload: AzureScriptMock = { responses = responses }
            let! _ = fakeAzureHttp.PostAsJsonAsync("/test/mock/llm-script", payload)
            return ()
        }

    /// Configures the fake chat-completions SSE streaming behavior: artificial delay before each
    /// chunk, mid-stream connection reset once N data lines were written (pair it with a nonzero
    /// chunkDelayMs so already-flushed chunks reach the client before the reset), and a Retry-After
    /// header (seconds) on scripted 429 responses. All zeros resets to defaults.
    member _.SetAzureLlmStreamOptions(chunkDelayMs: int, abortAfterChunks: int, retryAfterSeconds: int) =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let payload: AzureLlmStreamOptionsMock =
                { chunkDelayMs = chunkDelayMs
                  abortAfterChunks = abortAfterChunks
                  retryAfterSeconds = retryAfterSeconds }
            let! resp = fakeAzureHttp.PostAsJsonAsync("/test/mock/azure-llm-stream-options", payload)
            resp.EnsureSuccessStatusCode() |> ignore
        }

    /// Scripts the REACTION-triage chat-completions calls (separate queue from SetAzureLlmScript, so
    /// it never collides with text triage). Used to inject a 429 and assert the reaction path fails
    /// fast (one call, ERROR) instead of retrying into a storm. An empty array clears the script.
    member _.SetAzureReactionLlmScript(responses: AzureScriptedResponse array) =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let payload: AzureScriptMock = { responses = responses }
            let! _ = fakeAzureHttp.PostAsJsonAsync("/test/mock/reaction-llm-script", payload)
            return ()
        }

    /// Scripts the Azure OpenAI audio/transcriptions (STT) endpoint (AlitaBot voice
    /// transcription). An empty array clears the script (calls fall back to an empty transcript).
    member _.SetAzureSttScript(responses: AzureScriptedResponse array) =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let payload: AzureScriptMock = { responses = responses }
            let! _ = fakeAzureHttp.PostAsJsonAsync("/test/mock/stt-script", payload)
            return ()
        }

    /// Scripts the Azure OpenAI audio/speech (TTS) endpoint (AlitaBot /say, Slice 9
    /// stretch). An empty array clears the script (calls fall back to the fake's default
    /// "OggS"-prefixed bytes — see FakeAzureOcrApi.Store.defaultTtsBytes).
    member _.SetAzureTtsScript(responses: AzureScriptedResponse array) =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let payload: AzureScriptMock = { responses = responses }
            let! _ = fakeAzureHttp.PostAsJsonAsync("/test/mock/tts-script", payload)
            return ()
        }

    /// Scripts the images/generations + images/edits endpoints (AlitaBot image generation, S3)
    /// — one shared queue for both, dequeued per call. An empty array clears the script (calls
    /// fall back to the fake's default scripted tiny PNG).
    member _.SetAzureImageScript(responses: AzureScriptedResponse array) =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let payload: AzureScriptMock = { responses = responses }
            let! _ = fakeAzureHttp.PostAsJsonAsync("/test/mock/image-script", payload)
            return ()
        }

    /// Scripts the Azure OpenAI embeddings endpoint (AlitaBot's memory foundation, Slice 5a) —
    /// same shared-queue pattern as SetAzureImageScript. An empty array clears the script
    /// (calls fall back to the fake's deterministic hash-of-text vectors, Embedding.embed).
    member _.SetAzureEmbeddingsScript(responses: AzureScriptedResponse array) =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let payload: AzureScriptMock = { responses = responses }
            let! _ = fakeAzureHttp.PostAsJsonAsync("/test/mock/embeddings-script", payload)
            return ()
        }

    /// Scripts the Azure Responses API endpoint (`POST /openai/v1/responses`, AlitaBot's
    /// `web_search` NL tool, S10 PR1). An empty array clears the script (calls fall back to
    /// the fake's default "answer with no citations" body).
    member _.SetAzureResponsesScript(responses: AzureScriptedResponse array) =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let payload: AzureScriptMock = { responses = responses }
            let! _ = fakeAzureHttp.PostAsJsonAsync("/test/mock/responses-script", payload)
            return ()
        }

    /// Returns only the Azure Responses API calls the fake recorded (S10 PR1's `web_search`
    /// tool) — filters `GetAzureOcrCalls()` by url, same convention as `GetAzureEmbeddingsCalls`.
    member _.GetAzureResponsesCalls() =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let! resp = fakeAzureHttp.GetFromJsonAsync<FakeCall array>("/test/calls")
            return resp |> Array.filter (fun c -> c.Url.Contains("/openai/v1/responses"))
        }

    /// Scripts Gemini generateContent calls against an IMAGE model (AlitaBot Gemini
    /// provider slice, `/img` with IMAGE_PROVIDER=gemini) — model name contains "image",
    /// see FakeAzureOcrApi's Handlers.handleGeminiGenerateContent. An empty array clears
    /// the script (calls fall back to the fake's default scripted tiny PNG).
    member _.SetGeminiImageScript(responses: AzureScriptedResponse array) =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let payload: AzureScriptMock = { responses = responses }
            let! _ = fakeAzureHttp.PostAsJsonAsync("/test/mock/gemini-image-script", payload)
            return ()
        }

    /// Scripts Gemini generateContent calls against a MUSIC model (`/song`) — model name
    /// does not contain "image" (a Lyria model). An empty array clears the script (calls
    /// fall back to the fake's default scripted tiny WAV).
    member _.SetGeminiMusicScript(responses: AzureScriptedResponse array) =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let payload: AzureScriptMock = { responses = responses }
            let! _ = fakeAzureHttp.PostAsJsonAsync("/test/mock/gemini-music-script", payload)
            return ()
        }

    /// Returns only the Gemini generateContent calls the fake recorded (`/gemini/*` — same
    /// shared container as the Azure OCR/OpenAI fakes, see `GetAzureOcrCalls`).
    member _.GetGeminiCalls() =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let! resp = fakeAzureHttp.GetFromJsonAsync<FakeCall array>("/test/calls")
            return resp |> Array.filter (fun c -> c.Url.Contains("/gemini/"))
        }

    /// Returns only the Azure OpenAI embeddings calls the fake recorded.
    member _.GetAzureEmbeddingsCalls() =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let! resp = fakeAzureHttp.GetFromJsonAsync<FakeCall array>("/test/calls")
            return resp |> Array.filter (fun c -> c.Url.Contains("/embeddings"))
        }

    /// Returns only the Azure OpenAI chat-completions calls the fake recorded (filters out OCR).
    /// Tests count these to assert dedup/single-flight (e.g. "exactly 1 call for this content")
    /// and retry (e.g. ">= 2 calls" after a scripted 429).
    member _.GetAzureLlmCalls() =
        task {
            if not config.OcrEnabled then
                invalidOp "This fixture has OCR disabled (no FakeAzureOcrApi container)."
            let! resp = fakeAzureHttp.GetFromJsonAsync<FakeCall array>("/test/calls")
            // Chat-completions only — was "/openai/" (any Azure OpenAI route), which also
            // matched images/audio/embeddings. That was harmless before Slice 5a (nothing
            // else ran concurrently within a single test's window); the embedding pipeline
            // now fires a background /embeddings call on essentially every logged message,
            // which would otherwise silently join this list and break exact-count
            // assertions (e.g. LlmTests' `Assert.Single(llmCalls)`).
            return resp |> Array.filter (fun c -> c.Url.Contains("/chat/completions"))
        }

    /// Advances the bot's FakeTimeProvider by `ms` milliseconds, deterministically
    /// firing any pending TimeProvider-driven timers (notably BatchDebounce.Schedule).
    /// Requires TEST_MODE=true on the bot. Returns once the bot has accepted the
    /// advance — the timer callback work itself is async, so call should be followed
    /// by polling the DB / GetFakeCalls for the expected post-finalize state.
    member _.AdvanceBotClock(ms: int) =
        task {
            use content = new StringContent("", Encoding.UTF8, "application/json")
            let! resp = botHttps[0].PostAsync($"/test/clock/advance?ms={ms}", content)
            resp.EnsureSuccessStatusCode() |> ignore
            return ()
        }

    /// Advances EVERY instance's FakeTimeProvider by the same `ms`, in lockstep — required for
    /// N>1 fixtures whose cross-pod logic compares this-instance `now` against another
    /// instance's DB-persisted timestamp (e.g. CouponHubBot's batch debounce): advancing one
    /// instance alone would desync it from the others. For N=1 this is equivalent to
    /// AdvanceBotClock. Requires TEST_MODE=true.
    member _.AdvanceAllClocks(ms: int) =
        task {
            for i in 0 .. n - 1 do
                use content = new StringContent("", Encoding.UTF8, "application/json")
                let! resp = botHttps[i].PostAsync($"/test/clock/advance?ms={ms}", content)
                resp.EnsureSuccessStatusCode() |> ignore
        }

    /// Requires TEST_MODE=true on the bot. Clears TelegramMembershipService's
    /// in-memory cache so a subsequent IsMember() re-checks live instead of
    /// returning a stale cached verdict.
    member _.InvalidateMembershipCache() =
        task {
            use content = new StringContent("", Encoding.UTF8, "application/json")
            let! resp = botHttps[0].PostAsync("/test/membership/invalidate", content)
            resp.EnsureSuccessStatusCode() |> ignore
            return ()
        }

    /// GET /config-dump from instance `i` — the auth header is already the default request
    /// header on `botHttps[i]` (same SecretToken as the webhook secret). Raw JSON; secret
    /// fields are `{present:bool}` only — parse with JsonDocument per AGENTS.md's
    /// Cyrillic/JSON rule.
    member _.GetSettingsDump(i: int) =
        task {
            let! resp = botHttps[i].GetAsync("/config-dump")
            resp.EnsureSuccessStatusCode() |> ignore
            return! resp.Content.ReadAsStringAsync()
        }

    /// Stops and re-starts instance 0's bot app container, preserving postgres + fakes so
    /// DB state survives. Used for restart-recovery tests.
    member this.RestartBotApp() =
        task {
            do! botContainers[0].StopAsync()
            do! botContainers[0].StartAsync()
            botHttps[0].Dispose()
            let http = new HttpClient(BaseAddress = Uri($"http://127.0.0.1:{botContainers[0].GetMappedPublicPort(80)}"))
            http.Timeout <- TimeSpan.FromSeconds(15.0)
            http.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", config.SecretToken)
            botHttps[0] <- http
        }
