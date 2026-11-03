module VahterBanBot.LlmLayerConfiguration

open System
open System.Globalization

[<Literal>]
let private AzureSol56Setting = "azure-sol-5.6"

[<Literal>]
let TypesafeJevModel = "jev-1.14.0"

[<Literal>]
let private TypesafeJevSetting = "typesafe-" + TypesafeJevModel

[<RequireQualifiedAccess>]
type LlmLayerId =
    | AzureSol56
    | TypesafeJev114
    member this.SettingValue =
        match this with
        | LlmLayerId.AzureSol56 -> AzureSol56Setting
        | LlmLayerId.TypesafeJev114 -> TypesafeJevSetting
    static member Parse(value: string) =
        match value with
        | AzureSol56Setting -> LlmLayerId.AzureSol56
        | TypesafeJevSetting -> LlmLayerId.TypesafeJev114
        | _ -> invalidArg "LLM_TRIAGE_LAYERS" $"Unsupported LLM layer: {value}"

/// Preserves configured order and guarantees a non-empty sequence of unique, supported layers.
type LlmLayerOrder =
    private | LlmLayerOrder of LlmLayerId list
    member this.Layers = let (LlmLayerOrder layers) = this in layers
    static member Default = LlmLayerOrder [LlmLayerId.AzureSol56]
    static member Parse(values: string list) =
        match values with
        | [] -> invalidArg "LLM_TRIAGE_LAYERS" "At least one LLM layer is required."
        | _ ->
            let layers = values |> List.map LlmLayerId.Parse
            if Set.count (Set.ofList layers) <> List.length layers then
                invalidArg "LLM_TRIAGE_LAYERS" "Layer IDs must be unique."
            LlmLayerOrder layers

type JevConfiguration =
    private
        { endpoint: Uri
          timeout: TimeSpan
          minConfidence: float
          minProbability: float
          minMargin: float }
    member this.Endpoint = this.endpoint
    member this.Timeout = this.timeout
    member this.MinConfidence = this.minConfidence
    member this.MinProbability = this.minProbability
    member this.MinMargin = this.minMargin
    static member Parse(endpoint: string, timeoutMs: string, confidence: string, probability: string, margin: string) =
        let uri = Uri(endpoint, UriKind.Absolute)
        if (uri.Scheme <> "https" && uri.Scheme <> "http") || uri.UserInfo <> "" || uri.Query <> "" || uri.Fragment <> "" then
            invalidArg "JEV_ENDPOINT" "Expected an HTTP(S) endpoint without credentials, query or fragment."
        let timeout = Int32.Parse(timeoutMs, CultureInfo.InvariantCulture)
        if timeout < 1 || timeout > 30000 then invalidArg "JEV_TIMEOUT_MS" "Expected 1 through 30000 milliseconds."
        let score name (value: string) =
            let parsed = Double.Parse(value, CultureInfo.InvariantCulture)
            if not (Double.IsFinite parsed) || parsed < 0.0 || parsed > 1.0 then
                invalidArg name "Expected a finite number between 0 and 1."
            parsed
        { endpoint = uri
          timeout = TimeSpan.FromMilliseconds(float timeout)
          minConfidence = score "JEV_MIN_CONFIDENCE" confidence
          minProbability = score "JEV_MIN_PROBABILITY" probability
          minMargin = score "JEV_MIN_MARGIN" margin }
