namespace BotInfra

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

/// Shared OCR service backed by Azure Computer Vision.
[<AllowNullLiteral>]
type IBotOcr =
    abstract member TextFromImageBytes: imageBytes: ReadOnlyMemory<byte> -> Task<string>

/// Configuration for the shared OCR service. Bots populate from their own config records.
type BotOcrConfig =
    { OcrEnabled: bool
      OcrMaxFileSizeBytes: int64
      AzureOcrEndpoint: string
      AzureOcrKey: string }

type AzureBotOcr(httpClient: HttpClient, options: IOptions<BotOcrConfig>, logger: ILogger<AzureBotOcr>) =
    let mutable warnedDisabled = false

    let buildAnalyzeUri (baseEndpoint: string) =
        // Expect base resource URL like:
        //   https://<name>.cognitiveservices.azure.com
        // and we build the full request URL explicitly.
        //
        // If a full URL is provided, we still try to treat it as base (drop path/query).
        let baseUri = Uri(baseEndpoint.Trim())

        let normalizedBase =
            UriBuilder(baseUri.Scheme, baseUri.Host, baseUri.Port).Uri

        let analyze =
            Uri(normalizedBase, "/computervision/imageanalysis:analyze")

        let b = UriBuilder(analyze)
        // Required query for stream OCR
        b.Query <- "overload=stream&api-version=2024-02-01&features=read"
        b.Uri.ToString()

    interface IBotOcr with
        member _.TextFromImageBytes(imageBytes: ReadOnlyMemory<byte>) =
            task {
                let config = options.Value
                if not config.OcrEnabled then
                    if not warnedDisabled then
                        logger.LogWarning("OCR is disabled (OCR_ENABLED setting is false); Azure OCR call skipped")
                        warnedDisabled <- true
                    return null
                elif String.IsNullOrWhiteSpace config.AzureOcrEndpoint || String.IsNullOrWhiteSpace config.AzureOcrKey then
                    logger.LogWarning("OCR is enabled but Azure configuration is missing")
                    return null
                else
                    // reset the once-flag so a later toggle back to disabled will warn again
                    warnedDisabled <- false
                    try
                        use bytesContent = new ByteArrayContent(imageBytes.ToArray())
                        bytesContent.Headers.ContentType <- MediaTypeHeaderValue("application/octet-stream")
                        bytesContent.Headers.ContentLength <- imageBytes.Length

                        let url =
                            try buildAnalyzeUri config.AzureOcrEndpoint
                            with ex ->
                                logger.LogWarning(ex, "Invalid AZURE_OCR_ENDPOINT (expected base URL). Value: {Endpoint}", config.AzureOcrEndpoint)
                                config.AzureOcrEndpoint

                        use request = new HttpRequestMessage(HttpMethod.Post, url)
                        request.Headers.Add("Ocp-Apim-Subscription-Key", config.AzureOcrKey)
                        request.Content <- bytesContent

                        use! response = httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                        let! responseContent = response.Content.ReadAsStringAsync()

                        if response.IsSuccessStatusCode then
                            let text = AzureOcr.extractTextFromAzureResponse responseContent
                            if isNull text then
                                logger.LogWarning("Couldn't extract text from Azure OCR response: {Body}", responseContent)
                            return text
                        else
                            logger.LogWarning("Azure OCR returned status {Status}. Response: {Body}", response.StatusCode, responseContent)
                            return null
                    with ex ->
                        logger.LogError(ex, "Failed to extract text via Azure OCR")
                        return null
            }
