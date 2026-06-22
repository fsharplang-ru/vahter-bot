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

let private parseReactionTriageResponse (logger: ILogger) (json: string) =
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
        let reason =
            match inner.RootElement.TryGetProperty("reason") with
            | true, r when r.ValueKind = JsonValueKind.String -> Some (r.GetString())
            | _ -> None
        let usage = root.GetProperty("usage")
        let promptTokens     = usage.GetProperty("prompt_tokens").GetInt32()
        let completionTokens = usage.GetProperty("completion_tokens").GetInt32()
        Some (verdict, reason, promptTokens, completionTokens)
    with ex ->
        logger.LogWarning(ex, "Failed to parse reaction-triage LLM response. Raw body: {Body}", json)
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

// ── Reaction-spam triage classifier (vision-enabled) ──────────────────────────

/// All evidence we surface to both the LLM and (in shadow / UNSURE flow) the vahter alert.
/// Built once per threshold trip in Bot.fs so the two decisions are based on identical input.
type ReactionTriageDossier =
    { UserId:                   int64
      Username:                 string option
      DisplayName:              string
      Bio:                      string
      PhotoBytes:               byte[] option
      TotalMessagesAcrossChats: int
      FirstSeenAt:              DateTime option
      Last10Events:             DossierEvent array
      OriginatingChatId:        int64 }

/// Classifier verdict + token / latency metadata. Verdict has already been recorded as
/// LlmReactionTriageClassified by the time we return; the caller uses the structured value
/// to decide what action (BAN/SPAM/NOT_SPAM) to take or whether to fall through to vahter.
type ReactionTriageResult =
    { Verdict:    LlmReactionVerdict
      Reason:     string option
      ModelName:  string
      PromptHash: string }

type IReactionTriageClassifier =
    abstract member ModelName:  string
    abstract member PromptHash: string
    /// Classifies the dossier. Records a `LlmReactionTriageClassified` event with `shadowMode`
    /// set as given. Errors and parse failures resolve to `LlmReactionVerdict.Error` (and are
    /// recorded with verdict="ERROR").
    abstract member ClassifyReactionSpammer: dossier: ReactionTriageDossier * shadowMode: bool * ct: CancellationToken -> Task<ReactionTriageResult>

type AzureReactionTriage(httpClient: HttpClient, botConf: IOptions<BotConfiguration>, logger: ILogger<AzureReactionTriage>, db: DbService) =

    let staticSystemPrompt =
        """You are a spam-detection assistant for Russian-language IT Telegram chats. You are evaluating whether a user is an emoji-reaction spammer. These spammers join groups, place positive emoji reactions on random messages to surface their profile in notifications, and their profile picture + bio link are the actual ad. Treat these signals as load-bearing:

1. Bio contains a link or any advertising text — VERY STRONG spam signal (this is the actual ad vector).
2. Display name is "First Name + Last Name" in Russian (e.g. "Ольга Давыдова") — ~99% of these spammers use this pattern; real members of IT chats overwhelmingly use nicknames or first-name-only handles.
3. Profile photo of a young woman — social-engineering bait, almost universal in this attack pattern.
4. Zero or near-zero message history (0 messages across all chats, or a single short greeting like "привет") — a real lurker with this profile shape is implausible.

Verdict policy:
 - BAN      : 3+ signals are clearly present (especially bio-link + young-woman photo).
 - SPAM     : 2 signals are present but evidence is softer; remove reactions in this chat only.
 - NOT_SPAM : the user has a real conversation history or none of the signals fit.
 - UNSURE   : picture is unclear, profile is privacy-strict, or signals contradict — humans decide.

Respond with strict JSON: {"verdict":"BAN"|"SPAM"|"NOT_SPAM"|"UNSURE", "reason":"<one short sentence>"}"""

    let promptHash =
        SHA256.HashData(Encoding.UTF8.GetBytes(staticSystemPrompt))
        |> Convert.ToHexString
        |> _.ToLower()

    let modelName = botConf.Value.AzureOpenAiDeployment

    let formatDossier (d: ReactionTriageDossier) =
        let username = d.Username |> Option.map (fun u -> $"@{u}") |> Option.defaultValue "(none)"
        let firstSeen =
            match d.FirstSeenAt with
            | Some t -> t.ToString("yyyy-MM-dd HH:mm 'UTC'")
            | None   -> "(never seen before)"
        let bioLine = if String.IsNullOrWhiteSpace d.Bio then "(empty / privacy-strict)" else d.Bio
        let eventsLine =
            if d.Last10Events.Length = 0 then "(no recent events on record)"
            else
                d.Last10Events
                |> Array.map (fun e ->
                    let ts = e.created_at.ToString("MM-dd HH:mm")
                    match e.kind with
                    | "reaction" -> $"  • {ts} [chat {e.chat_id}] reacted to msg {e.message_id}"
                    | _ ->
                        let truncated = if isNull e.text then "(no text)" elif e.text.Length > 120 then e.text.Substring(0, 120) + "…" else e.text
                        $"  • {ts} [chat {e.chat_id}] message: {truncated}")
                |> String.concat "\n"
        sprintf "Username: %s\nDisplay name: %s\nFirst seen: %s\nTotal messages across all monitored chats: %d\n\nBio:\n%s\n\nLast %d events (newest first):\n%s\n\nOriginating chat: %d"
            username d.DisplayName firstSeen d.TotalMessagesAcrossChats bioLine d.Last10Events.Length eventsLine d.OriginatingChatId

    let buildContent (d: ReactionTriageDossier) =
        let dossierText = formatDossier d
        match d.PhotoBytes with
        | Some bytes ->
            let b64 = Convert.ToBase64String(bytes)
            // multimodal: text + image_url
            sprintf """[{"type":"text","text":%s},{"type":"image_url","image_url":{"url":"data:image/jpeg;base64,%s"}}]"""
                (JsonSerializer.Serialize(dossierText))
                b64
        | None ->
            // text-only — still wrap as content array for consistency
            sprintf """[{"type":"text","text":%s}]""" (JsonSerializer.Serialize(dossierText))

    interface IReactionTriageClassifier with
        member _.ModelName  = modelName
        member _.PromptHash = promptHash

        member _.ClassifyReactionSpammer(dossier: ReactionTriageDossier, shadowMode: bool, ct: CancellationToken) = task {
            use activity = botActivity.StartActivity("llmReactionTriage")
            if not (isNull activity) then
                %activity
                    .SetTag("user_id",      dossier.UserId)
                    .SetTag("chat_id",      dossier.OriginatingChatId)
                    .SetTag("shadow_mode",  shadowMode)
                    .SetTag("has_photo",    dossier.PhotoBytes.IsSome)
                    .SetTag("bio_present",  not (String.IsNullOrWhiteSpace dossier.Bio))

            let endpoint = botConf.Value.AzureOpenAiEndpoint
            // If endpoint or key is missing, return Error without calling — config issue, not a runtime failure.
            if String.IsNullOrWhiteSpace endpoint || String.IsNullOrWhiteSpace botConf.Value.AzureOpenAiKey then
                logger.LogWarning("Reaction triage skipped: Azure OpenAI endpoint/key is not configured")
                do! db.RecordLlmReactionTriageClassified(
                        dossier.OriginatingChatId, dossier.UserId, "ERROR", Some "config missing",
                        0, 0, 0, Some modelName, Some promptHash, shadowMode)
                return { Verdict = LlmReactionVerdict.Error; Reason = Some "config missing"; ModelName = modelName; PromptHash = promptHash }
            else

            let url = $"{endpoint}/openai/deployments/{modelName}/chat/completions?api-version=2024-08-01-preview"
            let userContent = buildContent dossier

            let requestJson =
                $"""{{
  "messages": [
    {{"role":"system","content":{JsonSerializer.Serialize(staticSystemPrompt)}}},
    {{"role":"user","content":{userContent}}}
  ],
  "response_format": {{
    "type": "json_schema",
    "json_schema": {{
      "name": "reaction_spam_verdict",
      "strict": true,
      "schema": {{
        "type": "object",
        "properties": {{
          "verdict": {{"type": "string", "enum": ["BAN","SPAM","NOT_SPAM","UNSURE"]}},
          "reason":  {{"type": "string"}}
        }},
        "required": ["verdict","reason"],
        "additionalProperties": false
      }}
    }}
  }},
  "max_tokens": 200,
  "temperature": 0
}}"""

            let sw = Stopwatch.StartNew()
            try
                use request = new HttpRequestMessage(HttpMethod.Post, url)
                request.Headers.Add("api-key", botConf.Value.AzureOpenAiKey)
                request.Content <- new StringContent(requestJson, Encoding.UTF8, "application/json")

                use! response = httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                let! body = response.Content.ReadAsStringAsync(ct)
                sw.Stop()

                if response.IsSuccessStatusCode then
                    match parseReactionTriageResponse logger body with
                    | Some (verdictStr, reason, promptTokens, completionTokens) ->
                        if not (isNull activity) then
                            %activity
                                .SetTag("verdict",      verdictStr)
                                .SetTag("latency_ms",   sw.ElapsedMilliseconds)
                                .SetTag("total_tokens", promptTokens + completionTokens)
                        do! db.RecordLlmReactionTriageClassified(
                                dossier.OriginatingChatId, dossier.UserId, verdictStr, reason,
                                promptTokens, completionTokens, int sw.ElapsedMilliseconds,
                                Some modelName, Some promptHash, shadowMode)
                        return { Verdict = LlmReactionVerdict.FromString verdictStr; Reason = reason; ModelName = modelName; PromptHash = promptHash }
                    | None ->
                        do! db.RecordLlmReactionTriageClassified(
                                dossier.OriginatingChatId, dossier.UserId, "ERROR", Some "parse failure",
                                0, 0, int sw.ElapsedMilliseconds, Some modelName, Some promptHash, shadowMode)
                        return { Verdict = LlmReactionVerdict.Error; Reason = Some "parse failure"; ModelName = modelName; PromptHash = promptHash }
                else
                    logger.LogWarning("Reaction triage returned {Status}: {Body}", int response.StatusCode, body)
                    do! db.RecordLlmReactionTriageClassified(
                            dossier.OriginatingChatId, dossier.UserId, "ERROR", Some (sprintf "HTTP %d" (int response.StatusCode)),
                            0, 0, int sw.ElapsedMilliseconds, Some modelName, Some promptHash, shadowMode)
                    return { Verdict = LlmReactionVerdict.Error; Reason = Some (sprintf "HTTP %d" (int response.StatusCode)); ModelName = modelName; PromptHash = promptHash }
            with ex ->
                sw.Stop()
                logger.LogWarning(ex, "Reaction triage HTTP call threw")
                do! db.RecordLlmReactionTriageClassified(
                        dossier.OriginatingChatId, dossier.UserId, "ERROR", Some "exception",
                        0, 0, int sw.ElapsedMilliseconds, Some modelName, Some promptHash, shadowMode)
                return { Verdict = LlmReactionVerdict.Error; Reason = Some "exception"; ModelName = modelName; PromptHash = promptHash }
        }
