module VahterBanBot.LlmTriage

// ChatReasoningEffortLevel is [Experimental("OPENAI001")], SetNewMaxCompletionTokensPropertyEnabled
// is [Experimental("AOAI001")] in the SDK (both FS0057) — used deliberately below.
#nowarn "57"

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
open Azure.AI.OpenAI.Chat
open OpenAI.Chat
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open VahterBanBot.Telemetry
open VahterBanBot.Types
open VahterBanBot.Utils
open VahterBanBot.LlmVerdictCache
open VahterBanBot.ProfileFetcher
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

// ── content_filter detection (shared by both triage paths) ────────────────────
//
// Azure's RAI (Responsible AI) policy can reject a chat-completion request outright with HTTP
// 400, error code "content_filter", when it judges the PROMPT itself severely harmful — e.g.
// incident aea4d561848519ad948d5d54eaea1b38 (2026-08-12): a spam photo whose OCR-appended text
// advertised CSAM categories. The response's `error.innererror` carries `content_filter_result`
// (per-category severity) and jailbreak/protected-material flags — none of which survive into
// `ClientResultException.Message`, so callers must go through `ex.GetRawResponse()` to recover
// them for logging.

/// Raw Azure error-response body for a `ClientResultException`, when the SDK still has it
/// buffered (it always does for a synchronously-thrown HTTP failure — `None` only guards
/// against a future SDK version that frees the response before throwing).
let private tryGetRawResponseBody (ex: ClientResultException) : string option =
    try
        match ex.GetRawResponse() with
        | null -> None
        | resp ->
            match resp.Content with
            | null -> None
            | content -> Some (content.ToString())
    with _ -> None

/// Best-effort extraction of `error.code` from a raw Azure error-response body.
let private tryParseErrorCode (body: string) =
    try
        use doc = JsonDocument.Parse(body)
        match doc.RootElement.TryGetProperty("error") with
        | true, err ->
            match err.TryGetProperty("code") with
            | true, c when c.ValueKind = JsonValueKind.String -> Some (c.GetString())
            | _ -> None
        | _ -> None
    with _ -> None

/// Detects Azure's "content_filter" rejection: HTTP 400 with `error.code = "content_filter"`.
/// Prefers parsing `rawBody` (from `tryGetRawResponseBody`) over substring-matching the
/// exception message, since the raw body is where `innererror`/`content_filter_result` live;
/// falls back to matching `exMessage` only when `rawBody` itself could not be recovered. Pulled
/// out as a pure function (status/body/message in, bool out) so it is unit-testable without
/// constructing a real `ClientResultException`.
let isContentFilterRejection (status: int) (rawBody: string option) (exMessage: string) : bool =
    status = 400 &&
    match rawBody with
    | Some body -> tryParseErrorCode body = Some "content_filter"
    | None      -> exMessage.Contains("content_filter")

/// Compact human-readable summary of which category (per `error.innererror.content_filter_result`)
/// actually fired, e.g. `"sexual=high, jailbreak"`. Only entries with `filtered=true` or
/// `detected=true` are included — a category present with `filtered=false` is not a trigger.
/// Entries carrying a `severity` (the four content categories: hate/self_harm/sexual/violence)
/// render as `"{category}={severity}"`; entries that only carry `detected` (jailbreak,
/// protected_material_text/code) render as bare `"{category}"`. Never throws — a missing/
/// unparsable body or `innererror`/`content_filter_result` falls back to a fixed placeholder
/// string, since a parse hiccup here must never break the (already-handled) content_filter flow.
let extractContentFilterTriggers (rawBody: string option) : string =
    let fallback = "unknown (no content_filter_result in response)"
    try
        match rawBody with
        | None -> fallback
        | Some body ->
            use doc = JsonDocument.Parse(body)
            match doc.RootElement.TryGetProperty("error") with
            | false, _ -> fallback
            | true, err ->
                match err.TryGetProperty("innererror") with
                | false, _ -> fallback
                | true, inner ->
                    match inner.TryGetProperty("content_filter_result") with
                    | false, _ -> fallback
                    | true, result when result.ValueKind = JsonValueKind.Object ->
                        let triggers =
                            result.EnumerateObject()
                            |> Seq.choose (fun prop ->
                                let obj = prop.Value
                                let isFiltered =
                                    match obj.TryGetProperty("filtered") with
                                    | true, f when f.ValueKind = JsonValueKind.True -> true
                                    | _ -> false
                                let isDetected =
                                    match obj.TryGetProperty("detected") with
                                    | true, d when d.ValueKind = JsonValueKind.True -> true
                                    | _ -> false
                                if not (isFiltered || isDetected) then None
                                else
                                    match obj.TryGetProperty("severity") with
                                    | true, s when s.ValueKind = JsonValueKind.String -> Some $"{prop.Name}={s.GetString()}"
                                    | _ -> Some prop.Name)
                            |> String.concat ", "
                        if triggers = "" then fallback else triggers
                    | true, _ -> fallback
    with _ -> fallback

/// Logs the raw Azure error-response body once, at Warning (this is now a handled signal, not
/// an unexpected fault) — joined to the rest of the request's logs by TraceId, per the "log the
/// payload once" house pattern. `pathLabel` distinguishes text triage from reaction triage in
/// the log line/Loki without needing two near-identical call sites. `triggers` is the
/// `extractContentFilterTriggers` summary — carried as its own structured property so it's
/// grep/filterable in Loki without parsing the raw body every time.
let private logContentFilterRejection (logger: ILogger) (pathLabel: string) (triggers: string) (rawBody: string option) =
    logger.LogWarning(
        "{TriagePath} content_filter rejection (HTTP 400): Azure RAI policy flagged the prompt as harmful. Triggers: {ContentFilterTriggers}. Raw response: {RawResponseBody}",
        pathLabel, triggers, defaultArg rawBody "(raw response body unavailable)")

// ── Empty-text media placeholder (message LLM triage only) ────────────────────
//
// Incident (2026-08-18, @AvaloniaRU, msg 217142): a caption-less spam-check-worthy sticker had
// no OCR text, so `msg.Text` stayed null. The LLM user-content rendered `Message:` with an EMPTY
// body (null interpolates to ""), so gpt-4o-mini judged a blank message on username/display-name
// alone and said SPAM — an innocent static cat sticker got auto-deleted. Real spammers DO post
// content-less stickers/photos with the spam in their NAME or BIO, so the fix is to give the LLM
// honest context about WHAT the message is, not to skip triage on empty text.
//
// CRITICAL: `mediaPlaceholder` is read ONLY when building the LLM prompt below — it must never be
// written back via `msg.AppendText`/`msg.PrependText`. `msg.Text` also feeds the ML scorer, the
// spam-text cache, the verdict-cache key (see `hasStableTextCacheKey` / Classify below), and the
// deleted-spam channel post. If the placeholder ever leaked into `msg.Text`, every photo/sticker
// would collapse onto the SAME cache key (e.g. "[photo, no readable text]") and — because
// SPAM/SKIP verdicts are cached GLOBALLY by text hash (see the module doc comment at the top of
// this file) — one SPAM verdict on a single photo would globally condemn every future photo.

/// Descriptive placeholder for the LLM prompt's `Message:` body when the message has no readable
/// text (`msg.Text` is null/empty) — `None` when there IS real text, in which case the caller
/// should render `msg.Text` as-is. Degrades gracefully when sticker emoji/set_name are absent
/// (the 2026-08-12 prod spam sticker had neither — see StickerOcrTests.fs).
let mediaPlaceholder (msg: TgMessage) : string option =
    if not (String.IsNullOrEmpty msg.Text) then None
    else
        match msg.Sticker with
        | Some s ->
            let emojiPart = s.Emoji |> Option.map (fun e -> $" \"{e}\"") |> Option.defaultValue ""
            let setPart = s.SetName |> Option.map (fun n -> $" from set \"{n}\"") |> Option.defaultValue ""
            Some $"[sticker{emojiPart}{setPart}, no readable text]"
        | None ->
            if msg.Photos.Length > 0 then
                Some "[photo, no readable text]"
            else
                // RawMessage is `internal` (same-assembly access only — see TgMessage.fs), so this
                // generic media check lives here rather than as a public TgMessage member.
                let raw = msg.RawMessage
                if raw.Video.IsSome then Some "[video, no readable text]"
                elif raw.Animation.IsSome then Some "[animation, no readable text]"
                elif raw.VideoNote.IsSome then Some "[video note, no readable text]"
                elif raw.Voice.IsSome then Some "[voice message, no readable text]"
                elif raw.Audio.IsSome then Some "[audio, no readable text]"
                elif raw.Document.IsSome then Some "[document, no readable text]"
                else Some "[empty message]"

/// Whether `msg.Text` alone yields a stable cache key — mirrors the guard `Classify` uses to pick
/// `NoCache` (LlmTriage.fs's `CacheRouting`). Pulled out as a pure predicate so "a placeholder-
/// rendered message still hits NoCache" is unit-testable without a live Azure client: `msg.Text`
/// is never mutated by `mediaPlaceholder` above, so a message that gets a placeholder in the
/// prompt still reports `false` here, exactly as before this change.
let hasStableTextCacheKey (msg: TgMessage) : bool =
    not (String.IsNullOrEmpty msg.Text)

/// Renders a fetched sender bio for the LLM prompt's "Bio:" line — `(none)` for null/empty/
/// whitespace, the bio text otherwise. `IUserProfileFetcher.Fetch` never throws (see
/// ProfileFetcher.fs) and already degrades any fetch failure to `Bio = ""`, so blank-vs-real is
/// the only distinction left to make here. Pulled out as a pure function purely for unit testing.
let formatBioLine (bio: string) : string =
    if String.IsNullOrWhiteSpace bio then "(none)" else bio

// ── Prompt v2 — repetition signal ──────────────────────────────────────────────
// Mirrors harness/reconstruct.py's format_repetition_line byte-for-byte (A/B-tested wording).

/// True when the text is long enough (>= 30 chars, the harness's threshold) to look up repetition.
let needsRepetitionLookup (textLength: int) : bool =
    textLength >= 30

/// Renders prompt v2's "Identical message seen earlier: ..." line. `repetition = None` only means
/// no lookup was made (see `needsRepetitionLookup`); a lookup that ran never returns `None`.
let formatRepetitionLine (textLength: int) (repetition: MessageRepetition option) : string =
    if not (needsRepetitionLookup textLength) then
        "Identical message seen earlier: not checked (short message)"
    else
        match repetition with
        | None -> "Identical message seen earlier: never"
        | Some r when r.total = 0 -> "Identical message seen earlier: never"
        | Some r ->
            $"Identical message seen earlier: {r.total} times in the last 30 days ({r.distinct_other_users} other users, {r.distinct_chats} chats)"

// ── gpt-5-family request parameters ─────────────────────────────────────────────

/// Request-shape decisions derived from `LLM_REASONING_EFFORT`, kept as a plain record so the
/// selection logic is unit-testable without a live SDK options object.
type LlmRequestParams =
    { Temperature: float32 option
      /// Bumped from 20 to 100 unconditionally — the old cap was too close to observed completion sizes.
      MaxOutputTokenCount: int
      ReasoningEffort: string option }

/// gpt-5-family reasoning models reject/ignore `temperature`; a non-empty `LLM_REASONING_EFFORT`
/// sends `reasoning_effort` and omits Temperature instead. Empty (default) keeps the pre-gpt-5
/// shape: Temperature=0, no reasoning_effort. Mirrors AlitaBot's AzureFoundryProvider.fs (~188)
/// precedent for the same class of model on a different provider.
let selectLlmRequestParams (reasoningEffort: string) : LlmRequestParams =
    if String.IsNullOrEmpty reasoningEffort then
        { Temperature = Some 0.0f; MaxOutputTokenCount = 100; ReasoningEffort = None }
    else
        { Temperature = None; MaxOutputTokenCount = 100; ReasoningEffort = Some reasoningEffort }

// ── Interface + implementation ────────────────────────────────────────────────

type ILlmTriage =
    abstract member ModelName:  string
    abstract member PromptHash: string
    abstract member Classify: msg: TgMessage * userMsgCount: int64 * ct: CancellationToken -> Task<LlmVerdict>

type AzureLlmTriage(botConf: IOptions<BotConfiguration>, logger: ILogger<AzureLlmTriage>, db: DbService, cache: ILlmVerdictCache, profileFetcher: IUserProfileFetcher) =

    // Coalesces concurrent identical-text classifications (same spam across channels at once).
    let inflight = ConcurrentDictionary<string, Lazy<Task<LlmVerdict>>>()
    // 3 attempts honoring Retry-After — message triage is deduped/single-flighted and can afford to wait.
    let clientCache = ChatClientCache(ClientRetryPolicy 3)

    // Static part of the system prompt — used to compute the prompt hash once at startup.
    // Per-chat descriptions are configuration, not the prompt itself.
    //
    // Prompt v2: A/B-tested artifact, copied byte-for-byte from the harness — rewording it
    // changes PromptHash and invalidates the cache and the measured A/B behavior.
    let staticSystemPrompt =
        """You are a spam detection assistant for a Telegram community.

Judge each message using ALL of these signals together:

1. Sender profile:
 - Advertising-style display names (e.g. "Зайди в мой био") are a strong spam signal.
 - A bio containing links, prices, or advertising is a spam signal.
 - Accounts with no @username, a generic display name and very few messages are the most common spammer profile.
 - A normal-looking @username and a history of messages (20+) are strong legitimacy signals.

2. Message count context (provided as "Total messages seen from this user"):
 - < 10 messages: new user — almost all spammers fall in this range
 - 10-20 messages: could be a hidden spammer who posted random stuff to blend in
 - 20-50 messages: most probably not a spammer — message must be really advertising something or be malicious

3. Repetition (provided as "Identical message seen earlier"):
 - If this exact text was already posted in our communities before — especially by OTHER users or in multiple chats — it is almost certainly a coordinated spam campaign. Treat 2+ prior sightings from a new user as SPAM even when the text looks like an innocent question.
 - Scammers disguise campaigns as ordinary personal questions (advice about documents, licenses, jobs, quick money) posted verbatim across many chats.

4. Chat topicality (see the "Chat:" line below when present):
 - Messages ON-TOPIC for this chat are almost never spam, even when long, technical, promotional-looking, or containing links. If the chat description explicitly allows a content type (job postings, event ads, selling parts, ticket resale), that content is legitimate — do not flag it.
 - An innocent-looking but clearly OFF-TOPIC personal question from a new user without @username is a known engagement-bait scam pattern — lean SPAM, or SKIP if unsure.

5. Contentless bait:
 - A contentless greeting ("Привет", "как дела?" and similar) carrying no actual question or content, sent as one of the first messages by a new user, is bait from bot accounts — the spam arrives later or gets edited in. Chat policy: such useless greetings are SPAM.

A media-only message with no readable text (rendered below as e.g. "[sticker ..., no readable
text]" or "[photo, no readable text]") is NOT, by itself, a spam signal — real spammers do this,
but so do ordinary members posting a reaction sticker/photo with nothing to OCR. For such
messages, judge only the sender signals (username, display name, bio); when those look normal,
prefer NOT_SPAM/SKIP.

Classify the message as exactly one of:
 - SPAM     : obvious advertising/bot/malicious content, or a campaign/bait pattern described above — delete and reduce user karma
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

        // Fetched only here — at the point of actual LLM escalation, not for every message.
        // IUserProfileFetcher.Fetch never throws (see ProfileFetcher.fs); an empty/missing bio
        // still degrades to "(none)" below.
        let! profile = profileFetcher.Fetch(msg.SenderId)
        let bio = formatBioLine profile.Bio

        // See the module doc comment above `mediaPlaceholder`: this placeholder is rendered ONLY
        // in the prompt string below — msg.Text itself is never touched, so the ML scorer / spam-
        // text cache / verdict-cache key / deleted-spam channel post all keep seeing the real
        // (empty) text.
        let messageBody = mediaPlaceholder msg |> Option.defaultValue msg.Text

        // Keyed off RAW msg.Text length, not messageBody's placeholder-rendered length — see formatRepetitionLine.
        let textLength = if isNull msg.Text then 0 else msg.Text.Length
        let! repetition =
            if needsRepetitionLookup textLength then
                task {
                    let! r = db.GetTextRepetition(msg.ChatId, msg.MessageId, msg.SenderId, msg.Text, 30)
                    return Some r
                }
            else
                Task.FromResult None
        let repetitionLine = formatRepetitionLine textLength repetition

        let userPrompt  =
            $"""Username: {username}
Display name: {displayName}
Bio: {bio}
Total messages seen from this user: {userMsgCount}
{repetitionLine}

Message:
{messageBody}"""

        // gpt-5-family request parameters — see selectLlmRequestParams's doc comment.
        let reqParams = selectLlmRequestParams botConf.Value.LlmReasoningEffort
        let options =
            let o =
                ChatCompletionOptions(
                    MaxOutputTokenCount = Nullable reqParams.MaxOutputTokenCount,
                    ResponseFormat      = ChatResponseFormat.CreateJsonSchemaFormat(
                                            "spam_verdict", spamVerdictSchema, jsonSchemaIsStrict = Nullable true))
            // Azure SDK sends legacy max_tokens unless opted in; gpt-5 family rejects it.
            o.SetNewMaxCompletionTokensPropertyEnabled(true)
            match reqParams.Temperature with
            | Some t -> o.Temperature <- Nullable t
            | None -> ()   // gpt-5-family rejects/ignores Temperature — omitted entirely, not sent as 0.
            match reqParams.ReasoningEffort with
            | Some effort -> o.ReasoningEffortLevel <- Nullable(ChatReasoningEffortLevel effort)
            | None -> ()
            o
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
            // Retries are exhausted by the time the SDK throws (429s aside). Three outcomes:
            //  - 429: expected sustained throttling — Error routes the message to human review.
            //  - 400 content_filter: a HANDLED signal (see below), not a fault — ContentFiltered,
            //    which Bot.fs's GetAutoVerdict turns into SPAM (or, with LLM_CONTENT_FILTER_IS_SPAM
            //    off, the same human-review fallback as Error).
            //  - anything else (other 400s, 401, 5xx, …): a real fault — Error routes the message
            //    to human review, same as before.
            sw.Stop()
            let rawBody = tryGetRawResponseBody ex
            if ex.Status = 429 then
                // Expected: sustained throttling after the retry budget. Not actionable on its own.
                logger.LogWarning(ex, "LLM triage throttled (429) after retries; routing to human review")
                return LlmVerdict.Error
            elif isContentFilterRejection ex.Status rawBody ex.Message then
                // Azure's RAI policy rejected the PROMPT itself as severely harmful. LLM triage
                // only runs when the ML score is already in the warning band, so this is
                // corroboration, not noise — treat it as a high-confidence SPAM signal. Log once
                // at Warning with the raw Azure response body verbatim (that's where
                // content_filter_result/innererror — per-category severity, jailbreak/
                // protected-material flags — live; the exception Message alone omits them).
                let triggers = extractContentFilterTriggers rawBody
                logContentFilterRejection logger "LLM triage" triggers rawBody
                return LlmVerdict.ContentFiltered triggers
            else
                // Unexpected: other 400s, 401, 5xx, … — a real fault that needs attention, not a
                // routine throttle or a handled content-filter case. Surface it at Error so it
                // isn't lost in warning noise.
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
            // (Unchanged by the media-placeholder prompt rendering above — hasStableTextCacheKey
            // reads msg.Text, never the placeholder; see that function's doc comment.)
            match (if hasStableTextCacheKey msg then Some (md5Hex msg.Text) else None) with
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
                let o =
                    ChatCompletionOptions(
                        Temperature         = Nullable 0.0f,
                        MaxOutputTokenCount = Nullable 200,
                        ResponseFormat      = ChatResponseFormat.CreateJsonSchemaFormat(
                                                "reaction_spam_verdict", reactionVerdictSchema, jsonSchemaIsStrict = Nullable true))
                // Azure SDK sends legacy max_tokens unless opted in; gpt-5 family rejects it.
                o.SetNewMaxCompletionTokensPropertyEnabled(true)
                o
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
                let rawBody = tryGetRawResponseBody ex
                if ex.Status = 429 then
                    // Expected: fail-fast throttle (this classifier runs with 0 retries by design).
                    logger.LogWarning(ex, "Reaction triage throttled (429) after {LatencyMs}ms", sw.ElapsedMilliseconds)
                elif isContentFilterRejection ex.Status rawBody ex.Message then
                    // 400 content_filter (e.g. a flagged profile photo) — verdict behavior is
                    // UNCHANGED here (still falls through to LlmReactionVerdict.Error below, same
                    // as any other failure); only the logging improves, so a future incident shows
                    // which category/severity/jailbreak flag fired instead of a bare "HTTP 400".
                    logContentFilterRejection logger "Reaction triage" (extractContentFilterTriggers rawBody) rawBody
                else
                    // Unexpected: other 400s, 401, 5xx, … — needs attention.
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
