namespace BotInfra

open System
open System.Diagnostics
open System.Threading
open System.Text.Json
open System.Threading.Tasks
open Azure
open Azure.AI.DocumentIntelligence
open Azure.Core.Pipeline
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

/// Completed OCR operation JSON and recognized text.
type OcrAnalysis =
    { RawJson: string
      Text: string }

[<AllowNullLiteral>]
type IBotOcr =
    abstract member AnalyzeImageBytes: imageBytes: ReadOnlyMemory<byte> -> Task<OcrAnalysis | null>

/// Configuration for the shared OCR service. Bots populate from their own config records.
type BotOcrConfig =
    { OcrEnabled: bool
      OcrMaxFileSizeBytes: int64
      AzureOcrEndpoint: string
      AzureOcrKey: string }

type AzureBotOcr(options: IOptions<BotOcrConfig>, logger: ILogger<AzureBotOcr>, transport: HttpPipelineTransport | null) =
    let mutable warnedDisabled = false

    let gate = obj()
    let mutable cached : (struct (string * string) * DocumentIntelligenceClient) option = None

    let endpointBase (raw: string) : Uri =
        let u = Uri(raw.Trim())
        UriBuilder(u.Scheme, u.Host, u.Port).Uri

    let getClient (endpoint: string) (key: string) : DocumentIntelligenceClient =
        let want = struct (endpoint, key)
        match cached with
        | Some (have, c) when have = want -> c
        | _ ->
            lock gate (fun () ->
                match cached with
                | Some (have, c) when have = want -> c
                | _ ->
                    let opts = DocumentIntelligenceClientOptions()
                    opts.Retry.MaxRetries <- 1
                    opts.Retry.NetworkTimeout <- TimeSpan.FromSeconds 3.
                    if not (isNull (box transport)) then
                        opts.Transport <- transport
                    let c = DocumentIntelligenceClient(endpointBase endpoint, AzureKeyCredential(key), opts)
                    cached <- Some (want, c)
                    c)

    let hasInvalidDimensions (failure: RequestFailedException) =
        try
            use body = JsonDocument.Parse(failure.GetRawResponse().Content.ToMemory())
            let data = BinaryData.FromString(body.RootElement.GetProperty("error").GetRawText())
            let error = System.ClientModel.Primitives.ModelReaderWriter.Read<DocumentIntelligenceError>(data)
            not (isNull error.InnerError) && error.InnerError.Code = "InvalidContentDimensions"
        with _ -> false

    interface IBotOcr with
        member _.AnalyzeImageBytes(imageBytes: ReadOnlyMemory<byte>) =
            task {
                let config = options.Value
                if not config.OcrEnabled then
                    if not warnedDisabled then
                        logger.LogWarning("OCR is disabled (OCR_ENABLED setting is false); Azure OCR call skipped")
                        warnedDisabled <- true
                    return (null: OcrAnalysis | null)
                elif String.IsNullOrWhiteSpace config.AzureOcrEndpoint || String.IsNullOrWhiteSpace config.AzureOcrKey then
                    logger.LogWarning("OCR is enabled but Azure configuration is missing")
                    return (null: OcrAnalysis | null)
                else
                    // reset the once-flag so a later toggle back to disabled will warn again
                    warnedDisabled <- false
                    try
                        let client = getClient config.AzureOcrEndpoint config.AzureOcrKey
                        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 20.)
                        let! operation =
                            client.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-read", BinaryData.FromBytes(imageBytes), deadline.Token)
                        let content = operation.Value.Content
                        let text = if String.IsNullOrWhiteSpace content then null else content
                        if isNull text then
                            logger.LogWarning("Azure OCR returned no readable text")
                        let rawJson = operation.GetRawResponse().Content.ToString()
                        return { RawJson = rawJson; Text = text }
                    with
                    | :? RequestFailedException as rfe when rfe.Status = 400 && hasInvalidDimensions rfe ->
                        match Activity.Current with
                        | null -> ()
                        | activity -> activity.SetTag("ocr.skipped", true).SetTag("ocr.skipReason", "invalid-image-dimensions") |> ignore
                        logger.LogDebug("Skipping OCR: image dimensions are outside Azure's supported range")
                        return (null: OcrAnalysis | null)
                    | :? RequestFailedException as rfe when rfe.Status > 0 ->
                        // The service responded with an HTTP error (e.g. 404 misconfigured endpoint,
                        // 403 VNet block, 500), already retried by the SDK where applicable. Logged at
                        // Error: a non-2xx that survives retries means OCR is broken (usually config or
                        // access), not a transient blip. We still degrade to "no usable OCR text" (null)
                        // so downstream uses the field-based classification ("no barcode"/"partial") —
                        // a ZXing-decoded barcode is still usable — rather than crashing the handler.
                        logger.LogError(rfe, "Azure OCR returned HTTP {Status}", rfe.Status)
                        return (null: OcrAnalysis | null)
                    | ex ->
                        // Transport failure (network/timeout, no HTTP response), already retried by the
                        // SDK. Re-throw so callers can tell a *backend failure* apart from a no-text
                        // result. Every caller wraps this in its own try/with, so re-throwing never
                        // crashes a handler. (reraise() isn't usable inside a task CE, so dispatch the
                        // captured exception preserving its stack.)
                        logger.LogWarning(ex, "Failed to reach Azure OCR")
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex)
                        return (null: OcrAnalysis | null)
            }
