module VahterBanBot.LlmLayerConfiguration

[<Literal>]
let private AzureSol56Setting = "azure-sol-5.6"

[<RequireQualifiedAccess>]
type LlmLayerId =
    | AzureSol56
    member this.SettingValue =
        match this with
        | LlmLayerId.AzureSol56 -> AzureSol56Setting
    static member Parse(value: string) =
        match value with
        | AzureSol56Setting -> LlmLayerId.AzureSol56
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
