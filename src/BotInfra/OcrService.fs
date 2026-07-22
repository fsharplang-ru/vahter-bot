namespace BotInfra

open System
open System.Text
open System.Threading.Tasks
open Azure
open Azure.AI.Vision.ImageAnalysis
open Azure.Core.Pipeline
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

/// Result of an Azure AI Vision OCR call. RawJson is the full Azure response body (preserved so
/// callers can mine other fields later or cache it as-is). Text is the extracted read text, joined
/// line-by-line — possibly null when the image legitimately contains no readable text.
type OcrAnalysis =
    { RawJson: string
      Text: string }

/// Shared OCR service backed by Azure AI Vision (Image Analysis).
[<AllowNullLiteral>]
type IBotOcr =
    abstract member AnalyzeImageBytes: imageBytes: ReadOnlyMemory<byte> -> Task<OcrAnalysis | null>

/// Configuration for the shared OCR service. Bots populate from their own config records.
type BotOcrConfig =
    { OcrEnabled: bool
      OcrMaxFileSizeBytes: int64
      AzureOcrEndpoint: string
      AzureOcrKey: string }

/// OCR via the Azure.AI.Vision.ImageAnalysis SDK (the "read" visual feature). The SDK owns the
/// request/response wire format and carries its own retry pipeline (Azure.Core — honors Retry-After,
/// and runs on the real wall-clock, so it is NOT coupled to any DI TimeProvider the way a Polly
/// pipeline is). `transport` is null in production (SDK default) and set by tests to route the SDK
/// through a stub HttpClient.
type AzureBotOcr(options: IOptions<BotOcrConfig>, logger: ILogger<AzureBotOcr>, transport: HttpPipelineTransport | null) =
    let mutable warnedDisabled = false

    // Memoized client per (endpoint, key). Both are hot-reloadable settings (read live from config on
    // every call), so rebuild only when the tuple changes. Thread-safe; rebuild runs only after a
    // settings change.
    let gate = obj()
    let mutable cached : (struct (string * string) * ImageAnalysisClient) option = None

    // The SDK builds the request path/query itself, so it needs the bare resource origin
    // (scheme://host[:port]) — not a full ".../imageanalysis:analyze?api-version=..." URL. Production
    // config historically stores the full analyze URL (the old hand-rolled client stripped it to the
    // host); handing that to the SDK doubles the path and Azure returns 404. Normalize defensively.
    let endpointBase (raw: string) : Uri =
        let u = Uri(raw.Trim())
        UriBuilder(u.Scheme, u.Host, u.Port).Uri

    let getClient (endpoint: string) (key: string) : ImageAnalysisClient =
        let want = struct (endpoint, key)
        match cached with
        | Some (have, c) when have = want -> c
        | _ ->
            lock gate (fun () ->
                match cached with
                | Some (have, c) when have = want -> c
                | _ ->
                    let opts = ImageAnalysisClientOptions()
                    // Bounded retry to fit a webhook handler: 1 retry, 2s per-attempt (NetworkTimeout).
                    // Honors Retry-After on 429. ~2s + backoff + 2s well under Telegram's webhook timeout.
                    opts.Retry.MaxRetries <- 1
                    opts.Retry.NetworkTimeout <- TimeSpan.FromSeconds 2.
                    if not (isNull (box transport)) then
                        opts.Transport <- transport
                    let c = ImageAnalysisClient(endpointBase endpoint, AzureKeyCredential(key), opts)
                    cached <- Some (want, c)
                    c)

    /// Joins the read result's lines into a single text blob (null when there is no readable text).
    let extractText (result: ImageAnalysisResult) : string =
        if isNull (box result.Read) then null
        else
            let sb = StringBuilder()
            for block in result.Read.Blocks do
                for line in block.Lines do
                    if not (String.IsNullOrWhiteSpace line.Text) then
                        sb.AppendLine(line.Text) |> ignore
            if sb.Length = 0 then null else sb.ToString().TrimEnd([| '\r'; '\n' |])

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
                        let! response = client.AnalyzeAsync(BinaryData.FromBytes(imageBytes.ToArray()), VisualFeatures.Read)
                        let text = extractText response.Value
                        if isNull text then
                            logger.LogWarning("Azure OCR returned no readable text")
                        let rawJson = response.GetRawResponse().Content.ToString()
                        return { RawJson = rawJson; Text = text }
                    with
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
