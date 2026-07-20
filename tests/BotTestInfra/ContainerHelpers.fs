module BotTestInfra.ContainerHelpers

open System
open System.IO
open System.Threading.Tasks
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Configurations
open DotNet.Testcontainers.Containers
open DotNet.Testcontainers.Images
open DotNet.Testcontainers.Networks
open Testcontainers.PostgreSql

// ── Image building with log capture ──────────────────────────────────────────

/// Configures an image builder with a StringLogger attached.
/// Returns the built IFutureDockerImage and the logger so callers can extract build output.
let buildImageSpec
    (solutionDir: CommonDirectoryPath)
    (dockerfile: string)
    (imageName: string)
    (forceRebuild: bool)
    (cleanUp: bool)
    (buildArgs: (string * string) list)
    =
    let logger = StringLogger()
    let mutable builder =
        ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionDir, String.Empty)
            .WithDockerfile(dockerfile)
            .WithName(imageName)
            .WithDeleteIfExists(true)
            .WithCleanUp(cleanUp)
            .WithLogger(logger)
    if forceRebuild then
        builder <- builder.WithBuildArgument("FORCE_REBUILD", DateTime.UtcNow.Ticks.ToString())
    for (key, value) in buildArgs do
        builder <- builder.WithBuildArgument(key, value)
    (builder.Build(), logger)

/// Builds an image, saves build logs to artifacts directory.
/// On failure, saves both the build log and an error file, then wraps the exception
/// with build logs so the error is visible in test output (not just in artifact files).
let buildImageWithLogs (artifactsDir: string) (name: string) (image: IFutureDockerImage) (logger: StringLogger) =
    let writeFile (path: string) (content: string) =
        Directory.CreateDirectory(artifactsDir) |> ignore
        File.WriteAllText(path, content)
    task {
        let logPath = Path.Combine(artifactsDir, $"{name}-build.log")
        try
            do! image.CreateAsync()
            let logs = logger.ExtractMessages()
            writeFile logPath logs
        with ex ->
            let logs = logger.ExtractMessages()
            writeFile logPath logs
            let errorPath = Path.Combine(artifactsDir, $"{name}-build-error.txt")
            let msg = $"Docker image build failed for {name}\n\nException: {ex.GetType().FullName}\nMessage: {ex.Message}\n\nFull:\n{ex}"
            writeFile errorPath msg
            let visibleMsg =
                $"Docker image build failed for '{name}', build logs:\n"
                + (if String.IsNullOrWhiteSpace logs then "<no logs captured>" else logs)
            raise (Exception(visibleMsg, ex))
    } :> Task

let private sharedImageSpecs = System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<IFutureDockerImage * StringLogger>>()
let private imageBuilds = System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task>>()

/// Returns the process-wide shared image spec for `imageName`, creating it via `mk` on first
/// use. Assembly fixtures initialize in parallel and use the same image names; each fixture
/// must reference the SAME IFutureDockerImage instance, because a container built from a
/// FutureDockerImage resolves the image name from that instance's created state.
let getOrCreateImageSpec (imageName: string) (mk: unit -> IFutureDockerImage * StringLogger) =
    sharedImageSpecs.GetOrAdd(imageName, fun _ -> Lazy<IFutureDockerImage * StringLogger>(valueFactory = Func<_>(mk))).Value

/// Builds each uniquely-named image at most once per test process, sharing the task across
/// assembly fixtures. Per-fixture delete-and-rebuild of the same tags duplicated build work
/// and raced: not every container engine allows deleting an image that another fixture's
/// in-flight build still holds (409 Conflict).
let buildImageOncePerProcess (imageName: string) (artifactsDir: string) (name: string) (image: IFutureDockerImage) (logger: StringLogger) : Task =
    imageBuilds.GetOrAdd(imageName, fun _ ->
        Lazy<Task>(fun () -> buildImageWithLogs artifactsDir name image logger)).Value

// ── Container factories ──────────────────────────────────────────────────────

let createNetwork () : INetwork =
    NetworkBuilder().Build()

let createPostgresContainer (network: INetwork) (alias: string) (pgImage: string) =
    PostgreSqlBuilder(pgImage)
        .WithNetwork(network)
        .WithNetworkAliases(alias)
        .Build()

let createFlywayContainer (network: INetwork) (migrationsPath: string) (dbAlias: string) (dbName: string) (dbContainer: PostgreSqlContainer) =
    ContainerBuilder("flyway/flyway")
        .WithNetwork(network)
        .WithBindMount(migrationsPath, "/flyway/sql", AccessMode.ReadOnly)
        // Without label=disable, hosts with SELinux enforcing block the container from
        // reading the bind-mounted repo files, and flyway silently skips /flyway/sql.
        // The option is a no-op where SELinux isn't in use (Ubuntu CI, Docker Desktop).
        .WithCreateParameterModifier(fun p -> p.HostConfig.SecurityOpt <- ResizeArray ["label=disable"])
        .WithEnvironment("FLYWAY_URL", $"jdbc:postgresql://{dbAlias}:5432/{dbName}")
        .WithEnvironment("FLYWAY_USER", "admin")
        .WithEnvironment("FLYWAY_PASSWORD", "admin")
        .WithCommand("migrate", "-schemas=public")
        .WithWaitStrategy(
            Wait.ForUnixContainer().AddCustomWaitStrategy(
                { new IWaitUntil with
                    member _.UntilAsync(container) =
                        task {
                            let! _ = container.GetExitCodeAsync()
                            return true
                        } }))
        .DependsOn(dbContainer)
        .Build()

let createFakeTgApiContainer (image: IFutureDockerImage) (network: INetwork) (alias: string) =
    ContainerBuilder(image)
        .WithNetwork(network)
        .WithNetworkAliases(alias)
        .WithPortBinding(8080, true)
        .WithEnvironment("ASPNETCORE_URLS", "http://*:8080")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(8080))
        .Build()

let createFakeAzureOcrContainer (image: IFutureDockerImage) (network: INetwork) (alias: string) =
    ContainerBuilder(image)
        .WithNetwork(network)
        .WithNetworkAliases(alias)
        .WithPortBinding(8081, true)
        .WithEnvironment("ASPNETCORE_URLS", "http://*:8081")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(8081))
        .Build()

// ── Log collection ───────────────────────────────────────────────────────────

/// Dumps stdout+stderr from a container to a log file under artifactsDir.
let dumpContainerLogs (artifactsDir: string) (containerName: string) (container: IContainer) =
    task {
        try
            let! struct (stdout, stderr) = container.GetLogsAsync()
            if not (isNull artifactsDir) then
                Directory.CreateDirectory(artifactsDir) |> ignore
                let path = Path.Combine(artifactsDir, $"{containerName}.log")
                let content = $"=== STDOUT ===\n{stdout}\n=== STDERR ===\n{stderr}\n"
                File.WriteAllText(path, content)
            return (stdout, stderr)
        with ex ->
            eprintfn $"Failed to get logs for {containerName}: {ex.Message}"
            return ("", "")
    }
