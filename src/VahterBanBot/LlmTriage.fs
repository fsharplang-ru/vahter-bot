module VahterBanBot.LlmTriage

open System
open System.Diagnostics
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open VahterBanBot.Telemetry
open VahterBanBot.Types
open VahterBanBot.Utils

// ── Response parsing ──────────────────────────────────────────────────────────

let private parseResponse (logger: ILogger) (json: string) =
    try
        use doc = JsonDocument.Parse(json)
        let root = doc.RootElement
        let content =
            root.GetProperty("choices").[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()
        use inner = JsonDocument.Parse(content)
        let verdict = inner.RootElement.GetProperty("verdict").GetString()
        let usage = root.GetProperty("usage")
        let promptTokens     = usage.GetProperty("prompt_tokens").GetInt32()
        let completionTokens = usage.GetProperty("completion_tokens").GetInt32()
        Some (verdict, promptTokens, completionTokens)
    with ex ->
        // Log raw body so unexpected response structures can be analyzed later
        logger.LogWarning(ex, "Failed to parse LLM triage response. Raw body: {Body}", json)
        None

// ── Interface + implementation ────────────────────────────────────────────────

type ILlmTriage =
    abstract member Classify: msg: TgMessage * userMsgCount: int64 * ct: CancellationToken -> Task<unit>

type AzureLlmTriage(httpClient: HttpClient, botConf: BotConfiguration, logger: ILogger<AzureLlmTriage>) =
    interface ILlmTriage with
        member _.Classify(msg: TgMessage, userMsgCount: int64, ct: CancellationToken) = task {
            if not botConf.LlmTriageEnabled then ()
            else

            use activity = botActivity.StartActivity("llmTriage")

            let chatDescLine =
                match botConf.LlmChatDescriptions.TryGetValue(msg.ChatId) with
                | true, d -> $"\nChat: {d}"
                | _       -> ""

            let systemPrompt =
                $"""You are a spam detection assistant for a Telegram community.{chatDescLine}
Watch for advertising-style display names (e.g. "Зайди в мой био") as a strong spam signal.
Classify the message as exactly one of:
 - KILL     : obvious advertising/bot/malicious content — permanent ban warranted
 - SPAM     : doesn't belong here, soft delete only
 - NOT_SPAM : legitimate message, false positive

Respond with exactly: {{"verdict":"KILL"}} or {{"verdict":"SPAM"}} or {{"verdict":"NOT_SPAM"}}"""

            let username    = if isNull msg.SenderUsername then "(none)" else $"@{msg.SenderUsername}"
            let displayName = msg.SenderDisplayName
            let userPrompt  =
                $"""Username: {username}
Display name: {displayName}
Total messages seen from this user: {userMsgCount}

Message:
{msg.Text}"""

            let requestJson =
                $"""{{
  "messages": [
    {{"role":"system","content":{JsonSerializer.Serialize(systemPrompt)}}},
    {{"role":"user","content":{JsonSerializer.Serialize(userPrompt)}}}
  ],
  "response_format": {{
    "type": "json_schema",
    "json_schema": {{
      "name": "spam_verdict",
      "strict": true,
      "schema": {{
        "type": "object",
        "properties": {{"verdict": {{"type": "string", "enum": ["KILL","SPAM","NOT_SPAM"]}}}},
        "required": ["verdict"],
        "additionalProperties": false
      }}
    }}
  }},
  "max_tokens": 20,
  "temperature": 0
}}"""

            let url = $"{botConf.AzureOpenAiEndpoint}/openai/deployments/{botConf.AzureOpenAiDeployment}/chat/completions?api-version=2024-08-01-preview"

            let sw = Stopwatch.StartNew()
            use request = new HttpRequestMessage(HttpMethod.Post, url)
            request.Headers.Add("api-key", botConf.AzureOpenAiKey)
            request.Content <- new StringContent(requestJson, Encoding.UTF8, "application/json")

            use! response = httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
            let! body = response.Content.ReadAsStringAsync(ct)
            sw.Stop()

            if response.IsSuccessStatusCode then
                match parseResponse logger body with
                | Some (verdict, promptTokens, completionTokens) ->
                    if not (isNull activity) then
                        %activity
                            .SetTag("verdict",      verdict)
                            .SetTag("latency_ms",   sw.ElapsedMilliseconds)
                            .SetTag("total_tokens", promptTokens + completionTokens)
                            .SetTag("chat_id",      msg.ChatId)
                            .SetTag("user_id",      msg.SenderId)
                    do! DB.insertLlmTriage msg.ChatId msg.MessageId msg.SenderId verdict promptTokens completionTokens (int sw.ElapsedMilliseconds)
                | None -> ()  // warning already logged in parseResponse
            else
                logger.LogWarning("LLM triage returned {Status}: {Body}", int response.StatusCode, body)
        }
