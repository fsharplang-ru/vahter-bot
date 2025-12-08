module VahterBanBot.ComputerVision

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open VahterBanBot.Types

let private extractTextFromAzureResponse (json: string) =
    try
        use doc = JsonDocument.Parse(json)
        let root = doc.RootElement
        match root.TryGetProperty("readResult") with
        | true, readResult ->
            match readResult.TryGetProperty("content") with
            | true, content -> content.GetString()
            | _ ->
                if readResult.TryGetProperty("blocks") |> fst then
                    readResult.GetProperty("blocks")
                    |> Seq.collect (fun block ->
                        match block.TryGetProperty("lines") with
                        | true, lines ->
                            lines
                            |> Seq.choose (fun line ->
                                match line.TryGetProperty("content") with
                                | true, content -> Some(content.GetString())
                                | _ -> None)
                        | _ -> Seq.empty)
                    |> String.concat "\n"
                    |> fun text -> if String.IsNullOrWhiteSpace text then null else text
                else null
        | _ -> null
    with _ -> null

[<AllowNullLiteral>]
type IComputerVision =
    abstract member TextFromImageUrl: url: string -> Task<string>

/// <summary>
/// Azure Computer Vision implementation. Uses streaming to avoid loading images into memory.
/// </summary>
type AzureComputerVision(httpClient: HttpClient, botConf: BotConfiguration, logger: ILogger<AzureComputerVision>) =
    interface IComputerVision with
        member _.TextFromImageUrl(url: string) = task {
            if not botConf.OcrEnabled then
                return null
            else
                if String.IsNullOrWhiteSpace botConf.AzureOcrEndpoint || String.IsNullOrWhiteSpace botConf.AzureOcrKey then
                    logger.LogWarning("OCR is enabled but Azure configuration is missing")
                    return null

                try
                    use! fileResponse = httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                    fileResponse.EnsureSuccessStatusCode() |> ignore

                    use! fileStream = fileResponse.Content.ReadAsStreamAsync()
                    use streamContent = new StreamContent(fileStream)
                    streamContent.Headers.ContentType <- MediaTypeHeaderValue("application/octet-stream")
                    match fileResponse.Content.Headers.ContentLength with
                    | Nullable length when length.HasValue ->
                        streamContent.Headers.ContentLength <- Nullable length.Value
                    | _ -> ()

                    use request = new HttpRequestMessage(HttpMethod.Post, botConf.AzureOcrEndpoint)
                    request.Headers.Add("Ocp-Apim-Subscription-Key", botConf.AzureOcrKey)
                    request.Content <- streamContent

                    use! response = httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                    let! responseContent = response.Content.ReadAsStringAsync()

                    if response.IsSuccessStatusCode then
                        let text = extractTextFromAzureResponse responseContent
                        return text
                    else
                        logger.LogWarning("Azure OCR returned status {Status}. Response: {Body}", response.StatusCode, responseContent)
                        return null
                with ex ->
                    logger.LogError(ex, "Failed to extract text via Azure OCR")
                    return null
        }
