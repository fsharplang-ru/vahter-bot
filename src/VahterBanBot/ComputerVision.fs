module VahterBanBot.ComputerVision

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open VahterBanBot.Types
open BotInfra

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
