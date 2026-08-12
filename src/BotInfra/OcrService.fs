namespace BotInfra

open System
open System.Diagnostics
open System.Text
open System.Text.Json
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
                    // Bounded retry to fit a webhook handler: 1 retry, 3s per-attempt (NetworkTimeout).
                    // Honors Retry-After on 429. ~3s + backoff + 3s well under Telegram's webhook timeout.
                    // Raised from 2s after a 2026-08-12 prod incident: a single "Failed to reach Azure
                    // OCR" TaskCanceledException hit exactly the 2s NetworkTimeout boundary (TLS-read
                    // stall, not slow OCR — direct calls to the same endpoint with the same production
                    // key measured 130-230ms end-to-end, well under even the old 2s). 2s left almost no
                    // margin above observed latency to absorb a brief network hiccup; 3s keeps the same
                    // shape (still a small multiple of real-world latency) with 50% more slack, and
                    // stays well clear of the 10s Azure-stall constant CouponHubBot.Tests' OCR-timeout
                    // fixtures (FakeAzureOcrApi "timeout" error mode) rely on to force both attempts to
                    // time out.
                    opts.Retry.MaxRetries <- 1
                    opts.Retry.NetworkTimeout <- TimeSpan.FromSeconds 3.
                    if not (isNull (box transport)) then
                        opts.Transport <- transport
                    let c = ImageAnalysisClient(endpointBase endpoint, AzureKeyCredential(key), opts)
                    cached <- Some (want, c)
                    c)

    /// Azure AI Vision's documented minimum: images below 50x50px are rejected outright with HTTP 400
    /// InvalidRequest / innererror.code=InvalidImageSize (a hard platform limit, not something we can
    /// configure around). Telegram legitimately sends tiny photos (e.g. a 60x42 sticker-like image), so
    /// this is an expected input shape, not a broken OCR backend — detected from the raw response body
    /// (the SDK only surfaces the outer "InvalidRequest" code via RequestFailedException.ErrorCode,
    /// which also covers unrelated 400s, so we look at the documented inner code instead).
    /// https://learn.microsoft.com/en-us/azure/ai-services/computer-vision/concept-describing-images#input-requirements
    let isImageTooSmallError (rfe: RequestFailedException) : bool =
        if rfe.Status <> 400 then false
        else
            try
                match rfe.GetRawResponse() with
                | null -> false
                | response ->
                    use doc = JsonDocument.Parse(response.Content.ToMemory())
                    match doc.RootElement.TryGetProperty("error") with
                    | false, _ -> false
                    | true, error ->
                        match error.TryGetProperty("innererror") with
                        | false, _ -> false
                        | true, innererror ->
                            match innererror.TryGetProperty("code") with
                            | true, code -> code.GetString() = "InvalidImageSize"
                            | false, _ -> false
            with _ ->
                // Malformed/unexpected body shape: fall through to the generic error-log path below
                // rather than silently swallowing a real failure.
                false

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
                    | :? RequestFailedException as rfe when rfe.Status = 400 && isImageTooSmallError rfe ->
                        // Image below Azure's hard 50x50px minimum (see isImageTooSmallError doc comment).
                        // Expected input, not an OCR backend failure: degrade to "no usable OCR text"
                        // quietly (Debug, not Error) and tag the ambient span so skips stay observable.
                        match Activity.Current with
                        | null -> ()
                        | activity -> activity.SetTag("ocr.skipped", true).SetTag("ocr.skipReason", "image-too-small") |> ignore
                        logger.LogDebug("Skipping OCR: image is below Azure's minimum dimensions (50x50px)")
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
