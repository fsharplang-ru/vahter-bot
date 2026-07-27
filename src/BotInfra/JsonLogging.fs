namespace BotInfra

open System.Text.Json
open Serilog.Core
open Serilog.Events

/// Wrapper marking a string as raw JSON that should be logged as a real nested
/// object in CLEF output (not an escaped string blob). Requires
/// RawJsonDestructuringPolicy to be registered (see Observability.configureSerilog).
type RawJson = RawJson of string

module JsonLogging =

    /// Past this depth a subtree is emitted as its raw text. Serilog's own depth
    /// limiter doesn't apply here (the policy builds the whole tree itself), so
    /// this is the only bound against pathological payloads.
    [<Literal>]
    let MaxDepth = 16

    /// JsonElement -> Serilog property-value tree.
    let rec ofJsonElement (depth: int) (el: JsonElement) : LogEventPropertyValue =
        if depth >= MaxDepth then
            ScalarValue(el.GetRawText())
        else
            match el.ValueKind with
            | JsonValueKind.Object ->
                // Materialize eagerly: the source JsonDocument is disposed right after conversion.
                let props =
                    [ for p in el.EnumerateObject() ->
                        // LogEventProperty rejects blank names
                        let name = if System.String.IsNullOrWhiteSpace p.Name then "_" else p.Name
                        LogEventProperty(name, ofJsonElement (depth + 1) p.Value) ]
                StructureValue(props)
            | JsonValueKind.Array ->
                SequenceValue [ for item in el.EnumerateArray() -> ofJsonElement (depth + 1) item ]
            | JsonValueKind.String -> ScalarValue(el.GetString())
            | JsonValueKind.Number ->
                // Int64 first: Telegram ids must not lose precision through double
                match el.TryGetInt64() with
                | true, i -> ScalarValue i
                | _ -> ScalarValue(el.GetDouble())
            | JsonValueKind.True -> ScalarValue true
            | JsonValueKind.False -> ScalarValue false
            | _ -> ScalarValue null

    /// Runs `log` with property `name` bound to the parsed JSON structure. The scope
    /// is deliberately tight so a large payload attaches only to the lines logged
    /// inside `log`, not to every line of the ambient async flow.
    let withRawJsonProperty (name: string) (json: string) (log: unit -> unit) : unit =
        use _ = Serilog.Context.LogContext.PushProperty(name, RawJson json, true)
        log ()

/// Destructures RawJson values into nested Serilog structures; malformed JSON
/// falls back to the original string as a scalar.
type RawJsonDestructuringPolicy() =
    interface IDestructuringPolicy with
        member _.TryDestructure(value, _factory, result) =
            match value with
            | :? RawJson as rawJson ->
                let (RawJson json) = rawJson
                result <-
                    try
                        use doc = JsonDocument.Parse json
                        JsonLogging.ofJsonElement 0 doc.RootElement
                    with :? JsonException ->
                        ScalarValue json
                true
            | _ -> false
