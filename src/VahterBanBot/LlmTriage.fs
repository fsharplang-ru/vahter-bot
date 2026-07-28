module VahterBanBot.LlmTriage

open System
open System.ClientModel
open System.ClientModel.Primitives
open System.Collections.Concurrent
open System.Diagnostics
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Azure.AI.OpenAI
open OpenAI.Chat
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open VahterBanBot.Telemetry
open VahterBanBot.Types
open VahterBanBot.Utils
open VahterBanBot.LlmVerdictCache
open BotInfra

// ── Dedup helpers ─────────────────────────────────────────────────────────────
//
// Verdict cache scoping (LLM_VERDICT_CACHE_GLOBAL_ENABLED, default ON — see BotConfiguration):
// SPAM/SKIP are cached GLOBALLY, keyed on the text hash alone (`text:global:{md5}`) — if a given
// text is spam (or triage-worthy) from one account, it is from another, so any sender's classified
// result is safe to serve to every subsequent sender with the same text. NOT_SPAM stays PER-SENDER
// (`text:{senderId}:{md5}`, the original key format) — this is the exonerating verdict, and sharing
// it across senders would let a spammer inherit an innocent user's clean verdict by simply reposting
// their text; that asymmetry is the entire safety argument, do not "simplify" it away. Error is
// never cached (transient failures must be retried, not pinned) — unchanged from before this split.
//
// Lookup precedence (see `Classify` below): the per-sender key is always checked FIRST. Since only
// NOT_SPAM is ever written there once the flag is on, a hit is always this sender's own prior
// exoneration and always wins — even if a DIFFERENT sender has since made the same text SPAM/SKIP
// globally. Only when the per-sender key misses do we fall through to the global key. This favors
// "don't re-punish a user we already cleared" over "a newer global verdict might supersede an older
// personal one" — a stale personal NOT_SPAM naturally falls out of consideration once its own TTL
// expires, same as any other cache entry, so the exposure window is bounded by
// LLM_VERDICT_CACHE_TTL_MINUTES either way. With the flag OFF, only the per-sender key exists for
// any verdict (the pre-PR behavior).

let private md5Hex (s: string) =
    MD5.HashData(Encoding.UTF8.GetBytes s) |> Convert.ToHexString |> _.ToLower()

/// How (and whether) `classifyUncached` should persist the verdict it produces.
///   - NoCache            : no stable key exists (e.g. empty-text/photo-only messages) — never persisted.
///   - SingleKey key      : LLM_VERDICT_CACHE_GLOBAL_ENABLED=false (legacy behavior) — every verdict,
///                          including SPAM/SKIP, is written to one sender-scoped key, exactly as before
///                          this split existed.
///   - SplitByVerdict     : flag on — SPAM/SKIP are written to the global (cross-sender) key so any
///     (globalKey,          other sender posting the same text is deduped from now on; NOT_SPAM is
///      senderKey)          written to the sender-scoped key only, so it is never inherited by a
///                          different sender (see the module doc comment above for the safety argument).
type private CacheRouting =
    | NoCache
    | SingleKey of key: string
    | SplitByVerdict of globalKey: string * senderKey: string

/// Coalesces concurrent calls with the same key onto a single in-flight Task, so a burst of
/// identical spam delivered across channels at once triggers only one Azure call (the rest
/// await the same result). The entry is removed once the task completes, so later (temporal)
/// repeats fall through to the DB verdict cache instead. Per-process — sufficient for the
/// single webhook instance.
let private singleFlight (inflight: ConcurrentDictionary<string, Lazy<Task<'T>>>) (key: string) (factory: unit -> Task<'T>) : Task<'T> =
    let entry =
        inflight.GetOrAdd(key, fun k ->
            lazy (task {
                try   return! factory()
                finally inflight.TryRemove(k) |> ignore
            }))
    entry.Value

// ── Azure OpenAI client (memoized, hot-reload aware) ──────────────────────────

/// Builds a `ChatClient` for the current (endpoint, key, deployment) and caches it, rebuilding only
/// when that tuple changes. All three are hot-reloadable settings (read live from BotConfiguration on
/// every call), so the client must NOT be frozen at startup — but rebuilding it per call is wasteful,
/// hence the memoization. The SDK's own retry pipeline (the supplied `ClientRetryPolicy`) honors
/// `Retry-After` on 429, which is what replaces the old Microsoft.Extensions.Http.Resilience handler.
/// The retry policy is supplied by the caller so each triage path can pick its own behavior. Thread-safe;
/// the rebuild path runs only right after a settings reload.
type private ChatClientCache(retryPolicy: ClientRetryPolicy) =
    let gate = obj()
    // Reference-typed option so the lock-free fast-path read below is atomic (no torn struct read).
    let mutable cached : (struct (string * string * string) * ChatClient) option = None
    member _.Get(endpoint: string, key: string, deployment: string) : ChatClient =
        let want = struct (endpoint, key, deployment)
        match cached with
        | Some (have, client) when have = want -> client
        | _ ->
            lock gate (fun () ->
                match cached with
                | Some (have, client) when have = want -> client
                | _ ->
                    let options = AzureOpenAIClientOptions(RetryPolicy = retryPolicy)
                    let client  = AzureOpenAIClient(Uri(endpoint), ApiKeyCredential(key), options)
                    let chat    = client.GetChatClient(deployment)
                    cached <- Some (want, chat)
                    chat)

// ── Response-format schemas (strict JSON) ─────────────────────────────────────

let private spamVerdictSchema =
    BinaryData.FromString """{"type":"object","properties":{"verdict":{"type":"string","enum":["SPAM","SKIP","NOT_SPAM"]}},"required":["verdict"],"additionalProperties":false}"""

let private reactionVerdictSchema =
    BinaryData.FromString """{"type":"object","properties":{"verdict":{"type":"string","enum":["BAN","SPAM","NOT_SPAM","UNSURE"]},"reason":{"type":"string"}},"required":["verdict","reason"],"additionalProperties":false}"""

// ── Response parsing ──────────────────────────────────────────────────────────
// The SDK hands back the assistant message text; with json_schema strict mode that text IS the
// verdict object, so we only parse the inner JSON (no chat-completions envelope to unwrap anymore).

let private parseVerdict (logger: ILogger) (content: string) =
    try
        use inner = JsonDocument.Parse(content)
        Some (inner.RootElement.GetProperty("verdict").GetString())
    with ex ->
        logger.LogWarning(ex, "Failed to parse LLM triage content. Raw: {Body}", content)
        None

let private parseVerdictAndReason (logger: ILogger) (content: string) =
    try
        use inner = JsonDocument.Parse(content)
        let verdict = inner.RootElement.GetProperty("verdict").GetString()
        let reason =
            match inner.RootElement.TryGetProperty("reason") with
            | true, r when r.ValueKind = JsonValueKind.String -> Some (r.GetString())
            | _ -> None
        Some (verdict, reason)
    with ex ->
        logger.LogWarning(ex, "Failed to parse reaction-triage content. Raw: {Body}", content)
        None

// ── Interface + implementation ────────────────────────────────────────────────

type ILlmTriage =
    abstract member ModelName:  string
    abstract member PromptHash: string
    abstract member Classify: msg: TgMessage * userMsgCount: int64 * ct: CancellationToken -> Task<LlmVerdict>

type AzureLlmTriage(botConf: IOptions<BotConfiguration>, logger: ILogger<AzureLlmTriage>, db: DbService, cache: ILlmVerdictCache) =

    // Coalesces concurrent identical-text classifications (same spam across channels at once).
    let inflight = ConcurrentDictionary<string, Lazy<Task<LlmVerdict>>>()
    // 3 attempts honoring Retry-After — message triage is deduped/single-flighted and can afford to wait.
    let clientCache = ChatClientCache(ClientRetryPolicy 3)

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

    // Performs the actual Azure call (no dedup/cache). On a successful verdict it records the
    // classification and, per `cacheRouting`, stores the verdict for reuse (routed by verdict when
    // `SplitByVerdict` — see that type's doc comment). Errors are never cached, so a transient 429
    // is retried next time rather than pinned.
    let classifyUncached (msg: TgMessage) (userMsgCount: int64) (cacheRouting: CacheRouting) (ct: CancellationToken) = task {
        use activity = botActivity.StartActivity("llmTriage")

        // endpoint/key/deployment are hot-reloadable — read live for this call.
        let modelName = botConf.Value.AzureOpenAiDeployment

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

        let options =
            ChatCompletionOptions(
                Temperature         = Nullable 0.0f,
                MaxOutputTokenCount = Nullable 20,
                ResponseFormat      = ChatResponseFormat.CreateJsonSchemaFormat(
                                        "spam_verdict", spamVerdictSchema, jsonSchemaIsStrict = Nullable true))
        let messages : ChatMessage[] =
            [| SystemChatMessage(systemPrompt)
               UserChatMessage(userPrompt) |]

        let sw = Stopwatch.StartNew()
        try
            let chatClient =
                clientCache.Get(botConf.Value.AzureOpenAiEndpoint, botConf.Value.AzureOpenAiKey, modelName)
            let! result = chatClient.CompleteChatAsync(messages, options, ct)
            sw.Stop()
            let content = result.Value.Content.[0].Text
            match parseVerdict logger content with
            | Some verdictStr ->
                let promptTokens     = result.Value.Usage.InputTokenCount
                let completionTokens = result.Value.Usage.OutputTokenCount
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
                match cacheRouting with
                | NoCache -> ()
                | SingleKey k -> do! cache.Save(k, verdictStr, None, Some modelName)
                | SplitByVerdict (globalKey, senderKey) ->
                    // SPAM/SKIP are safe to share cross-sender (no exoneration to leak) → global key.
                    // NOT_SPAM (and any unexpected value — schema enforces the 3 verdicts, so this is
                    // defensive only) stays sender-scoped, never shared.
                    let key = match verdictStr with "SPAM" | "SKIP" -> globalKey | _ -> senderKey
                    do! cache.Save(key, verdictStr, None, Some modelName)
                return LlmVerdict.FromString verdictStr
            | None ->
                // warning already logged in parseVerdict
                return LlmVerdict.Error
        with
        | :? ClientResultException as ex ->
            // Retries are exhausted by the time the SDK throws. Either way we fail safe:
            // Error routes the message to human review rather than letting it through.
            sw.Stop()
            if ex.Status = 429 then
                // Expected: sustained throttling after the retry budget. Not actionable on its own.
                logger.LogWarning(ex, "LLM triage throttled (429) after retries; routing to human review")
            else
                // Unexpected: 400 content_filter, 401, 5xx, … — a real fault that needs attention,
                // not a routine throttle. Surface it at Error so it isn't lost in warning noise.
                logger.LogError(ex, "LLM triage UNEXPECTED ERROR: HTTP {Status}", ex.Status)
            return LlmVerdict.Error
        | ex ->
            sw.Stop()
            logger.LogWarning(ex, "LLM triage call failed")
            return LlmVerdict.Error
    }

    interface ILlmTriage with
        member _.ModelName  = botConf.Value.AzureOpenAiDeployment
        member _.PromptHash = promptHash

        member _.Classify(msg: TgMessage, userMsgCount: int64, ct: CancellationToken) = task {
            if not botConf.Value.LlmTriageEnabled then return LlmVerdict.Skip
            else

            // Photo-only / empty-text messages have no stable text key → classify directly, no cache.
            match (if String.IsNullOrEmpty msg.Text then None else Some (md5Hex msg.Text)) with
            | None -> return! classifyUncached msg userMsgCount NoCache ct
            | Some hash ->
                let senderKey = sprintf "text:%d:%s" msg.SenderId hash

                // In-process single-flight coalescing stays scoped to (sender, text) — NOT widened to
                // the global text-only key — even though the persisted cache below now has a global
                // tier. Until classifyUncached returns we don't know the verdict, and a NOT_SPAM
                // result is only valid for the sender whose specific prompt (username/display
                // name/message count) actually produced it. Widening the in-flight key would let a
                // concurrently-racing DIFFERENT sender receive that NOT_SPAM straight out of memory,
                // before it ever reaches the DB — the exact cross-sender leak the split cache exists
                // to prevent, just one step earlier. So single-flight only ever coalesces true
                // duplicate delivery of the SAME sender's own message (unchanged from before this
                // PR); cross-sender dedup for SPAM/SKIP happens through the persisted global cache
                // tier, which is safe to share because it never carries an exonerating verdict. (A
                // consequence: a genuinely concurrent millisecond-scale burst of the same text from
                // MANY different senders still makes one Azure call per distinct sender until the
                // first classification is persisted — only sequential/TTL-window dedup is global.)
                return! singleFlight inflight senderKey (fun () -> task {
                    let ttl = TimeSpan.FromMinutes(float botConf.Value.LlmVerdictCacheTtlMinutes)

                    // Tier 1, always checked first (see module doc comment for the full precedence
                    // rationale): this sender's own prior verdict for this exact text always wins.
                    match! cache.TryGet(senderKey, ttl) with
                    | Some cv -> return LlmVerdict.FromString cv.Verdict
                    | None ->
                        if botConf.Value.LlmVerdictCacheGlobalEnabled then
                            // Tier 2: any sender's prior SPAM/SKIP for this exact text.
                            let globalKey = sprintf "text:global:%s" hash
                            match! cache.TryGet(globalKey, ttl) with
                            | Some cv -> return LlmVerdict.FromString cv.Verdict
                            | None -> return! classifyUncached msg userMsgCount (SplitByVerdict(globalKey, senderKey)) ct
                        else
                            // Feature flag OFF: revert to the pre-PR behavior — one key, one tier,
                            // scoped to (sender, text), for every verdict including SPAM/SKIP.
                            return! classifyUncached msg userMsgCount (SingleKey senderKey) ct
                })
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

type AzureReactionTriage(botConf: IOptions<BotConfiguration>, logger: ILogger<AzureReactionTriage>, db: DbService) =

    // Fail fast on throttle: NO retries. A reaction burst must not hold the webhook in a 429
    // Retry-After backoff (that is what burned the 60s timeout per reaction in the storm). The SDK's
    // ClientRetryPolicy retry loop runs through non-virtual internals, so we cannot subclass it to skip
    // only 429s — and in practice 429 is the only retryable error reaction triage has ever hit (30d of
    // prod: zero transient 5xx), so 0 retries is exactly "fail fast on throttle" for this workload.
    // A 429 now surfaces immediately as ClientResultException ("HTTP 429"). Re-triage is gated by the
    // per-user reaction-triage debounce.
    let clientCache = ChatClientCache(ClientRetryPolicy 0)

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

    /// Builds the user turn — multimodal (text + profile photo) when a photo is available, text-only
    /// otherwise. The image goes as an inline data part so no URL fetch is needed.
    let buildUserMessage (d: ReactionTriageDossier) : UserChatMessage =
        let dossierText = formatDossier d
        match d.PhotoBytes with
        | Some bytes ->
            UserChatMessage(
                [| ChatMessageContentPart.CreateTextPart(dossierText)
                   ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(bytes), "image/jpeg") |])
        | None ->
            UserChatMessage(ChatMessageContentPart.CreateTextPart(dossierText))

    interface IReactionTriageClassifier with
        member _.ModelName  = botConf.Value.AzureOpenAiDeployment
        member _.PromptHash = promptHash

        // Reaction triage relies on the SDK's retry pipeline (ClientRetryPolicy, honors Retry-After) to
        // survive 429s; it deliberately does NOT use the verdict cache — re-triage is already governed by
        // the per-user cooldown in RunReactionTriagePipeline.
        member _.ClassifyReactionSpammer(dossier: ReactionTriageDossier, shadowMode: bool, ct: CancellationToken) = task {
            use activity = botActivity.StartActivity("llmReactionTriage")
            if not (isNull activity) then
                %activity
                    .SetTag("user_id",      dossier.UserId)
                    .SetTag("chat_id",      dossier.OriginatingChatId)
                    .SetTag("shadow_mode",  shadowMode)
                    .SetTag("has_photo",    dossier.PhotoBytes.IsSome)
                    .SetTag("bio_present",  not (String.IsNullOrWhiteSpace dossier.Bio))

            // endpoint/key/deployment are hot-reloadable — read live for this call.
            let modelName = botConf.Value.AzureOpenAiDeployment
            let endpoint  = botConf.Value.AzureOpenAiEndpoint
            let key       = botConf.Value.AzureOpenAiKey

            // If endpoint or key is missing, return Error without calling — config issue, not a runtime failure.
            if String.IsNullOrWhiteSpace endpoint || String.IsNullOrWhiteSpace key then
                logger.LogWarning("Reaction triage skipped: Azure OpenAI endpoint/key is not configured")
                do! db.RecordLlmReactionTriageClassified(
                        dossier.OriginatingChatId, dossier.UserId, "ERROR", Some "config missing",
                        0, 0, 0, Some modelName, Some promptHash, shadowMode)
                return { Verdict = LlmReactionVerdict.Error; Reason = Some "config missing"; ModelName = modelName; PromptHash = promptHash }
            else

            let options =
                ChatCompletionOptions(
                    Temperature         = Nullable 0.0f,
                    MaxOutputTokenCount = Nullable 200,
                    ResponseFormat      = ChatResponseFormat.CreateJsonSchemaFormat(
                                            "reaction_spam_verdict", reactionVerdictSchema, jsonSchemaIsStrict = Nullable true))
            let messages : ChatMessage[] =
                [| SystemChatMessage(staticSystemPrompt)
                   buildUserMessage dossier |]

            let sw = Stopwatch.StartNew()
            try
                let chatClient = clientCache.Get(endpoint, key, modelName)
                let! result = chatClient.CompleteChatAsync(messages, options, ct)
                sw.Stop()
                let content = result.Value.Content.[0].Text
                match parseVerdictAndReason logger content with
                | Some (verdictStr, reason) ->
                    let promptTokens     = result.Value.Usage.InputTokenCount
                    let completionTokens = result.Value.Usage.OutputTokenCount
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
            with
            | :? ClientResultException as ex ->
                // Record the real status, not a generic "exception".
                sw.Stop()
                if ex.Status = 429 then
                    // Expected: fail-fast throttle (this classifier runs with 0 retries by design).
                    logger.LogWarning(ex, "Reaction triage throttled (429) after {LatencyMs}ms", sw.ElapsedMilliseconds)
                else
                    // Unexpected: 400 content_filter (e.g. a flagged profile photo), 401, 5xx, … — needs attention.
                    logger.LogError(ex, "Reaction triage UNEXPECTED ERROR: HTTP {Status} after {LatencyMs}ms", ex.Status, sw.ElapsedMilliseconds)
                let reason = sprintf "HTTP %d" ex.Status
                do! db.RecordLlmReactionTriageClassified(
                        dossier.OriginatingChatId, dossier.UserId, "ERROR", Some reason,
                        0, 0, int sw.ElapsedMilliseconds, Some modelName, Some promptHash, shadowMode)
                return { Verdict = LlmReactionVerdict.Error; Reason = Some reason; ModelName = modelName; PromptHash = promptHash }
            | ex ->
                sw.Stop()
                logger.LogWarning(ex, "Reaction triage call failed after {LatencyMs}ms", sw.ElapsedMilliseconds)
                let reason =
                    match ex with
                    | :? OperationCanceledException -> sprintf "canceled after %dms" sw.ElapsedMilliseconds
                    | _ ->
                        let m = ex.Message
                        sprintf "%s: %s" (ex.GetType().Name) (if m.Length > 160 then m.Substring(0, 160) else m)
                do! db.RecordLlmReactionTriageClassified(
                        dossier.OriginatingChatId, dossier.UserId, "ERROR", Some reason,
                        0, 0, int sw.ElapsedMilliseconds, Some modelName, Some promptHash, shadowMode)
                return { Verdict = LlmReactionVerdict.Error; Reason = Some reason; ModelName = modelName; PromptHash = promptHash }
        }
