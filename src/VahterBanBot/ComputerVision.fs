module VahterBanBot.ComputerVision

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open VahterBanBot.Types

let private extractTextFromAzureResponse (json: string) =
    try
        use doc = JsonDocument.Parse(json)
        let root = doc.RootElement

        let sb = StringBuilder()

        let addLine (text: string) =
            if not (String.IsNullOrWhiteSpace text) then
                sb.AppendLine(text) |> ignore

        let linesFromBlocks (readResult: JsonElement) =
            match readResult.TryGetProperty("blocks") with
            | true, blocks ->
                blocks.EnumerateArray()
                |> Seq.iter (fun block ->
                    match block.TryGetProperty("lines") with
                    | true, lines ->
                        lines.EnumerateArray()
                        |> Seq.iter (fun lineElement ->
                            match lineElement.TryGetProperty("text") with
                            | true, text -> addLine (text.GetString())
                            | _ ->
                                match lineElement.TryGetProperty("content") with
                                | true, content -> addLine (content.GetString())
                                | _ -> ())
                    | _ -> ())
            | _ -> ()

        match root.TryGetProperty("readResult") with
        | true, readResult ->
            match readResult.TryGetProperty("content") with
            | true, content -> addLine (content.GetString())
            | _ -> ()

            linesFromBlocks readResult

            if sb.Length = 0 then null else sb.ToString().TrimEnd([|'\r'; '\n'|])
        | _ -> null
    with _ -> null

[<AllowNullLiteral>]
type IComputerVision =
    abstract member TextFromImageUrl: url: string -> Task<string>

type AzureComputerVision(httpClient: HttpClient, botConf: BotConfiguration, logger: ILogger<AzureComputerVision>) =
    interface IComputerVision with
        member _.TextFromImageUrl(url: string) = task {
                if not botConf.OcrEnabled then
                    return null
                else
                    if String.IsNullOrWhiteSpace botConf.AzureOcrEndpoint || String.IsNullOrWhiteSpace botConf.AzureOcrKey then
                        logger.LogWarning("OCR is enabled but Azure configuration is missing")
                        return null
                    else

                    try
                        use! fileResponse = httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                        fileResponse.EnsureSuccessStatusCode() |> ignore

                        use! fileStream = fileResponse.Content.ReadAsStreamAsync()
                        use streamContent = new StreamContent(fileStream)
                        streamContent.Headers.ContentType <- MediaTypeHeaderValue("application/octet-stream")
                        if fileResponse.Content.Headers.ContentLength.HasValue then
                            streamContent.Headers.ContentLength <- fileResponse.Content.Headers.ContentLength.Value

                        use request = new HttpRequestMessage(HttpMethod.Post, botConf.AzureOcrEndpoint)
                        request.Headers.Add("Ocp-Apim-Subscription-Key", botConf.AzureOcrKey)
                        request.Content <- streamContent

                        use! response = httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                        let! responseContent = response.Content.ReadAsStringAsync()

                        if response.IsSuccessStatusCode then
                            let text = extractTextFromAzureResponse responseContent
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
