module VahterBanBot.Unit.Tests.RawJsonDestructuringTests

open System
open System.IO
open System.Text.Json
open BotInfra
open Serilog
open Serilog.Formatting.Compact
open Xunit

/// Minimal sink: formats each event with the real CLEF formatter into a StringWriter.
type private CollectingSink(sw: StringWriter) =
    let formatter = RenderedCompactJsonFormatter()
    interface Core.ILogEventSink with
        member _.Emit(logEvent) = formatter.Format(logEvent, sw)

let private makeLogger (sw: StringWriter) =
    LoggerConfiguration()
        .Enrich.FromLogContext()
        .Destructure.With<RawJsonDestructuringPolicy>()
        .WriteTo.Sink(CollectingSink sw)
        .CreateLogger()

/// Logs one event through the real CLEF formatter and parses the emitted line back.
let private renderLine (writeLog: Core.Logger -> unit) : JsonDocument =
    use sw = new StringWriter()
    let logger = makeLogger sw
    writeLog logger
    logger.Dispose()
    JsonDocument.Parse(sw.ToString())

[<Fact>]
let ``raw json is rendered as a real nested object, not an escaped string`` () =
    use doc =
        renderLine (fun log ->
            log.Information("upd {@RawUpdate}", RawJson """{"update_id":123,"message":{"chat":{"id":-1,"username":"flood"}}}"""))
    let raw = doc.RootElement.GetProperty "RawUpdate"
    Assert.Equal(JsonValueKind.Object, raw.ValueKind)
    Assert.Equal(123L, raw.GetProperty("update_id").GetInt64())
    Assert.Equal(-1L, raw.GetProperty("message").GetProperty("chat").GetProperty("id").GetInt64())
    Assert.Equal("flood", raw.GetProperty("message").GetProperty("chat").GetProperty("username").GetString())

[<Fact>]
let ``int64 ids survive without precision loss`` () =
    let bigId = 123456789012345678L
    use doc = renderLine (fun log -> log.Information("upd {@RawUpdate}", RawJson $"""{{"id":{bigId}}}"""))
    Assert.Equal(bigId, doc.RootElement.GetProperty("RawUpdate").GetProperty("id").GetInt64())

[<Fact>]
let ``arrays, bools, floats and nulls are preserved`` () =
    use doc =
        renderLine (fun log ->
            log.Information("upd {@RawUpdate}", RawJson """{"arr":[1,"two",true,null],"score":0.5}"""))
    let raw = doc.RootElement.GetProperty "RawUpdate"
    let arr = raw.GetProperty "arr"
    Assert.Equal(JsonValueKind.Array, arr.ValueKind)
    Assert.Equal(1L, arr[0].GetInt64())
    Assert.Equal("two", arr[1].GetString())
    Assert.Equal(JsonValueKind.True, arr[2].ValueKind)
    Assert.Equal(JsonValueKind.Null, arr[3].ValueKind)
    Assert.Equal(0.5, raw.GetProperty("score").GetDouble())

[<Fact>]
let ``malformed json falls back to the original string`` () =
    use doc = renderLine (fun log -> log.Information("upd {@RawUpdate}", RawJson "{not json"))
    let raw = doc.RootElement.GetProperty "RawUpdate"
    Assert.Equal(JsonValueKind.String, raw.ValueKind)
    Assert.Equal("{not json", raw.GetString())

[<Fact>]
let ``depth is capped, deeper subtree is emitted as raw text`` () =
    let deep = String.replicate 30 """{"a":""" + "1" + String.replicate 30 "}"
    use doc = renderLine (fun log -> log.Information("upd {@RawUpdate}", RawJson deep))
    // walk down until the structure stops nesting — the cap must have replaced it with a string
    let mutable el = doc.RootElement.GetProperty "RawUpdate"
    let mutable depth = 0
    while el.ValueKind = JsonValueKind.Object do
        el <- el.GetProperty "a"
        depth <- depth + 1
    Assert.Equal(JsonValueKind.String, el.ValueKind)
    Assert.Equal(JsonLogging.MaxDepth, depth)

[<Fact>]
let ``withRawJsonProperty attaches the nested payload via LogContext`` () =
    use doc =
        renderLine (fun log ->
            JsonLogging.withRawJsonProperty "RawUpdate" """{"update_id":7}""" (fun () ->
                log.Information "Received Telegram update"))
    Assert.Equal(7L, doc.RootElement.GetProperty("RawUpdate").GetProperty("update_id").GetInt64())

[<Fact>]
let ``withRawJsonProperty scope does not leak to later log lines`` () =
    use sw = new StringWriter()
    let logger = makeLogger sw
    JsonLogging.withRawJsonProperty "RawUpdate" """{"update_id":7}""" (fun () -> logger.Information "inside")
    logger.Information "outside"
    logger.Dispose()
    let lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
    use inside = JsonDocument.Parse lines[0]
    use outside = JsonDocument.Parse lines[1]
    Assert.True(inside.RootElement.TryGetProperty("RawUpdate") |> fst)
    Assert.False(outside.RootElement.TryGetProperty("RawUpdate") |> fst)
