module VahterBanBot.LlmTriage

open System
open System.Diagnostics
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open VahterBanBot.Telemetry
open VahterBanBot.Types
open VahterBanBot.Utils
open BotInfra

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
    abstract member ModelName:  string
    abstract member PromptHash: string
    abstract member Classify: msg: TgMessage * userMsgCount: int64 * ct: CancellationToken -> Task<LlmVerdict>

type AzureLlmTriage(httpClient: HttpClient, botConf: IOptions<BotConfiguration>, logger: ILogger<AzureLlmTriage>, db: DbService) =

    // Static part of the system prompt — used to compute the prompt hash once at startup.
    // Per-chat descriptions are configuration, not the prompt itself.
    let staticSystemPrompt =
        """You are a spam detection assistant for a Telegram community.
Watch for advertising-style display names (e.g. "Зайди в мой био") as a strong spam signal.

Message count context (provided as "Total messages seen from this user"):
 - < 10 messages: new user — almost all spammers fall in this range
 - 10-20 messages: could be a hidden spammer who posted random stuff to blend in
 - 20-50 messages: most probably not a spammer — message must be really advertising something or be malicious

Classify the message as exactly one of:
 - SPAM     : obvious advertising/bot/malicious content — delete and reduce user karma
 - SKIP     : not sure — route to human moderators for review
 - NOT_SPAM : legitimate message, false positive

In case of doubt, select SKIP.

Respond with exactly: {"verdict":"SPAM"} or {"verdict":"SKIP"} or {"verdict":"NOT_SPAM"}"""

    let promptHash =
        SHA256.HashData(Encoding.UTF8.GetBytes(staticSystemPrompt))
        |> Convert.ToHexString
        |> _.ToLower()

    let modelName = botConf.Value.AzureOpenAiDeployment

    interface ILlmTriage with
        member _.ModelName  = modelName
        member _.PromptHash = promptHash

        member _.Classify(msg: TgMessage, userMsgCount: int64, ct: CancellationToken) = task {
            if not botConf.Value.LlmTriageEnabled then return LlmVerdict.Skip
            else

            use activity = botActivity.StartActivity("llmTriage")

            let chatDescLine =
                match botConf.Value.LlmChatDescriptions.TryGetValue(msg.ChatId) with
                | true, d -> $"\nChat: {d}"
                | _       -> ""

            let systemPrompt =
                $"""{staticSystemPrompt}{chatDescLine}"""

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
        "properties": {{"verdict": {{"type": "string", "enum": ["SPAM","SKIP","NOT_SPAM"]}}}},
        "required": ["verdict"],
        "additionalProperties": false
      }}
    }}
  }},
  "max_tokens": 20,
  "temperature": 0
}}"""

            let url = $"{botConf.Value.AzureOpenAiEndpoint}/openai/deployments/{botConf.Value.AzureOpenAiDeployment}/chat/completions?api-version=2024-08-01-preview"

            let sw = Stopwatch.StartNew()
            use request = new HttpRequestMessage(HttpMethod.Post, url)
            request.Headers.Add("api-key", botConf.Value.AzureOpenAiKey)
            request.Content <- new StringContent(requestJson, Encoding.UTF8, "application/json")

            use! response = httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
            let! body = response.Content.ReadAsStringAsync(ct)
            sw.Stop()

            if response.IsSuccessStatusCode then
                match parseResponse logger body with
                | Some (verdictStr, promptTokens, completionTokens) ->
                    if not (isNull activity) then
                        %activity
                            .SetTag("verdict",      verdictStr)
                            .SetTag("latency_ms",   sw.ElapsedMilliseconds)
                            .SetTag("total_tokens", promptTokens + completionTokens)
                            .SetTag("chat_id",      msg.ChatId)
                            .SetTag("user_id",      msg.SenderId)
                    do! db.RecordLlmClassified(
                            msg.ChatId, msg.MessageId, verdictStr,
                            promptTokens, completionTokens, int sw.ElapsedMilliseconds,
                            Some modelName, Some promptHash)
                    return LlmVerdict.FromString verdictStr
                | None ->
                    // warning already logged in parseResponse
                    return LlmVerdict.Error
            else
                logger.LogWarning("LLM triage returned {Status}: {Body}", int response.StatusCode, body)
                return LlmVerdict.Error
        }
