namespace FakeAzureOcrApi

open System
open System.Net
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.AspNetCore.Http

module Handlers =
    let readBody (ctx: HttpContext) =
        task {
            if ctx.Request.ContentLength.HasValue && ctx.Request.ContentLength.Value = 0L then
                return ""
            else
                use reader = new IO.StreamReader(ctx.Request.Body, Encoding.UTF8)
                return! reader.ReadToEndAsync()
        }

    let respondJson (ctx: HttpContext) (status: int) (json: string) =
        task {
            ctx.Response.StatusCode <- status
            ctx.Response.ContentType <- "application/json"
            let bytes = Encoding.UTF8.GetBytes(json)
            do! ctx.Response.Body.WriteAsync(bytes.AsMemory(0, bytes.Length))
        }

    let handleAnalyze (ctx: HttpContext) =
        task {
            let url = ctx.Request.Path.ToString() + ctx.Request.QueryString.ToString()
            let! body = readBody ctx
            Console.WriteLine($"FAKE AZURE IN  {ctx.Request.Method} {url} bodyLen={body.Length}")
            Store.logCall ctx.Request.Method url body

            // Per-call scripted response wins over static, if any are queued.
            let scripted =
                let mutable item = Unchecked.defaultof<ScriptedResponse>
                if Store.responseScript.TryDequeue(&item) then Some item else None

            let status, respBody, delayMs, errorMode =
                match scripted with
                | Some s ->
                    let em = if isNull (box s.errorMode) then "" else s.errorMode
                    s.status, s.body, s.delayMs, em
                | None -> Store.responseStatus, Store.responseBody, Store.responseDelayMs, Store.responseErrorMode

            if delayMs > 0 then
                do! Task.Delay(delayMs)

            match errorMode with
            | "network" ->
                // Abort the request so the client sees a TCP-level failure → HttpRequestException.
                ctx.Abort()
            | "timeout" ->
                // Stall well past the client's HttpClient.Timeout so it cancels first.
                do! Task.Delay(10_000)
                do! respondJson ctx status respBody
            | _ ->
                do! respondJson ctx status respBody
        }

    /// Adds a Retry-After header to 429s when configured (chat-completions only).
    let private respondLlmJson (ctx: HttpContext) (status: int) (json: string) =
        task {
            if status = 429 && Store.llmRetryAfterSeconds > 0 then
                ctx.Response.Headers["Retry-After"] <- Microsoft.Extensions.Primitives.StringValues(string Store.llmRetryAfterSeconds)
            do! respondJson ctx status json
        }

    /// Streams a full chat-completion JSON as SSE: content split into ~3 delta chunks,
    /// then a finish_reason chunk, a usage chunk (empty choices), and `data: [DONE]`.
    /// Honors Store.llmStreamChunkDelayMs (delay before each data line) and
    /// Store.llmStreamAbortAfterChunks (connection reset once N data lines were written —
    /// the reset happens on the NEXT write, so already-written chunks can reach the client).
    let private respondChatCompletionSse (ctx: HttpContext) (completionJson: string) =
        task {
            let content, model, finishReason, usageJson =
                try
                    use doc = JsonDocument.Parse(completionJson)
                    let root = doc.RootElement
                    let model =
                        match root.TryGetProperty "model" with
                        | true, m when m.ValueKind = JsonValueKind.String ->
                            m.GetString() |> Option.ofObj |> Option.defaultValue "gpt-fake"
                        | _ -> "gpt-fake"
                    let choice0 =
                        match root.TryGetProperty "choices" with
                        | true, cs when cs.ValueKind = JsonValueKind.Array && cs.GetArrayLength() > 0 -> Some cs[0]
                        | _ -> None
                    let content =
                        choice0
                        |> Option.bind (fun c ->
                            match c.TryGetProperty "message" with
                            | true, m -> (match m.TryGetProperty "content" with
                                          | true, v when v.ValueKind = JsonValueKind.String -> Option.ofObj (v.GetString())
                                          | _ -> None)
                            | _ -> None)
                        |> Option.defaultValue ""
                    let finish =
                        choice0
                        |> Option.bind (fun c ->
                            match c.TryGetProperty "finish_reason" with
                            | true, v when v.ValueKind = JsonValueKind.String -> Option.ofObj (v.GetString())
                            | _ -> None)
                        |> Option.defaultValue "stop"
                    let usage =
                        match root.TryGetProperty "usage" with
                        | true, u when u.ValueKind = JsonValueKind.Object -> u.GetRawText()
                        | _ -> """{"prompt_tokens":0,"completion_tokens":0,"total_tokens":0}"""
                    content, model, finish, usage
                with _ ->
                    completionJson, "gpt-fake", "stop", """{"prompt_tokens":0,"completion_tokens":0,"total_tokens":0}"""

            ctx.Response.StatusCode <- 200
            ctx.Response.ContentType <- "text/event-stream"

            let esc (s: string) = JsonSerializer.Serialize(s)
            let emitted = ref 0
            let aborted = ref false
            let writeData (payload: string) =
                task {
                    if not aborted.Value then
                        if Store.llmStreamChunkDelayMs > 0 then
                            do! Task.Delay(Store.llmStreamChunkDelayMs)
                        if Store.llmStreamAbortAfterChunks > 0 && emitted.Value >= Store.llmStreamAbortAfterChunks then
                            ctx.Abort()
                            aborted.Value <- true
                        else
                            let bytes = Encoding.UTF8.GetBytes($"data: {payload}\n\n")
                            do! ctx.Response.Body.WriteAsync(bytes.AsMemory(0, bytes.Length))
                            do! ctx.Response.Body.FlushAsync()
                            emitted.Value <- emitted.Value + 1
                }

            let parts =
                if content.Length = 0 then [||]
                else
                    let chunkCount = 3
                    let size = max 1 ((content.Length + chunkCount - 1) / chunkCount)
                    [| for i in 0 .. size .. content.Length - 1 -> content.Substring(i, min size (content.Length - i)) |]

            for part in parts do
                do! writeData $"""{{"id":"chatcmpl-fake-stream","object":"chat.completion.chunk","model":{esc model},"choices":[{{"index":0,"delta":{{"content":{esc part}}},"finish_reason":null}}]}}"""
            do! writeData $"""{{"id":"chatcmpl-fake-stream","object":"chat.completion.chunk","model":{esc model},"choices":[{{"index":0,"delta":{{}},"finish_reason":{esc finishReason}}}]}}"""
            do! writeData $"""{{"id":"chatcmpl-fake-stream","object":"chat.completion.chunk","model":{esc model},"choices":[],"usage":{usageJson}}}"""
            do! writeData "[DONE]"
        }

    /// Fake Azure OpenAI Chat Completions handler.
    ///
    /// Two routing modes — picked by which json_schema.name the request asks for:
    ///
    /// 1. `spam_verdict` (existing message triage) — content is a plain string. Routes by keyword:
    ///    "kill" → SPAM, "spam" → SKIP, otherwise → NOT_SPAM. Response: `{"verdict":"..."}`
    ///
    /// 2. `reaction_spam_verdict` (new reaction triage) — content is an array (multimodal).
    ///    We grep the raw request body for keywords. Response: `{"verdict":"...", "reason":"..."}`.
    ///    Keywords in display name / bio steer the verdict so tests can assert each branch:
    ///       - "ban-me"        → BAN
    ///       - "react-spam"    → SPAM
    ///       - "real-lurker"   → NOT_SPAM
    ///       - otherwise       → UNSURE
    let handleChatCompletions (ctx: HttpContext) =
        task {
            let url = ctx.Request.Path.ToString() + ctx.Request.QueryString.ToString()
            let! body = readBody ctx
            Console.WriteLine($"FAKE OPENAI IN {ctx.Request.Method} {url} bodyLen={body.Length}")
            Store.logCall ctx.Request.Method url body

            let isReactionTriage =
                body.Contains("reaction_spam_verdict", StringComparison.Ordinal)

            // SSE mode: the client asked for a streamed completion ("stream": true).
            // Non-streaming callers (vahter/coupon via Azure SDK) are unaffected.
            let streamRequested =
                try
                    use doc = JsonDocument.Parse(body)
                    match doc.RootElement.TryGetProperty "stream" with
                    | true, s -> s.ValueKind = JsonValueKind.True
                    | _ -> false
                with _ -> false

            // Per-call scripted response wins over keyword routing, if any are queued. This lets
            // tests inject HTTP 429s (and "network"/"timeout") to exercise retry/backoff and the
            // failure-fallthrough behavior. Text triage (spam_verdict) and reaction triage
            // (reaction_spam_verdict) draw from SEPARATE queues, so one path can never consume the
            // other's scripted entry. When a queue empties, calls fall back to keyword routing below.
            let scripted =
                let queue = if isReactionTriage then Store.reactionLlmResponseScript else Store.llmResponseScript
                let mutable item = Unchecked.defaultof<ScriptedResponse>
                if queue.TryDequeue(&item) then Some item else None

            if scripted.IsSome then
                let s = scripted.Value
                if s.delayMs > 0 then do! Task.Delay(s.delayMs)
                match (if isNull (box s.errorMode) then "" else s.errorMode) with
                | "network" -> ctx.Abort()
                | "timeout" ->
                    do! Task.Delay(10_000)
                    do! respondJson ctx s.status s.body
                | _ when streamRequested && s.status = 200 -> do! respondChatCompletionSse ctx s.body
                | _ -> do! respondLlmJson ctx s.status s.body
            else

            let responseJson =
                if isReactionTriage then
                    let verdict =
                        if body.Contains("ban-me", StringComparison.OrdinalIgnoreCase) then "BAN"
                        elif body.Contains("react-spam", StringComparison.OrdinalIgnoreCase) then "SPAM"
                        elif body.Contains("real-lurker", StringComparison.OrdinalIgnoreCase) then "NOT_SPAM"
                        else "UNSURE"
                    $"""{{
  "choices": [{{
    "finish_reason": "stop",
    "index": 0,
    "message": {{
      "content": "{{\"verdict\":\"{verdict}\",\"reason\":\"fake handler routed by keyword\"}}",
      "role": "assistant"
    }}
  }}],
  "created": 1774736361,
  "id": "chatcmpl-fake-reaction",
  "model": "gpt-4o-mini-2024-07-18",
  "object": "chat.completion",
  "usage": {{
    "completion_tokens": 12,
    "prompt_tokens": 480,
    "total_tokens": 492
  }}
}}"""
                else
                    let verdict =
                        try
                            use doc = JsonDocument.Parse(body)
                            let msgs = doc.RootElement.GetProperty("messages")
                            let userContent =
                                msgs.EnumerateArray()
                                |> Seq.tryFind (fun m ->
                                    match m.TryGetProperty("role") with
                                    | true, role -> role.GetString() = "user"
                                    | _ -> false)
                                |> Option.bind (fun m ->
                                    match m.TryGetProperty("content") with
                                    | true, c -> Some (c.GetString())
                                    | _ -> None)
                                |> Option.bind Option.ofObj
                                |> Option.defaultValue ""
                            if userContent.Contains("kill", StringComparison.OrdinalIgnoreCase) then "SPAM"
                            elif userContent.Contains("spam", StringComparison.OrdinalIgnoreCase) then "SKIP"
                            else "NOT_SPAM"
                        with _ -> "NOT_SPAM"
                    $"""{{
  "choices": [{{
    "finish_reason": "stop",
    "index": 0,
    "message": {{
      "content": "{{\"verdict\":\"{verdict}\"}}",
      "role": "assistant"
    }}
  }}],
  "created": 1774736361,
  "id": "chatcmpl-fake",
  "model": "gpt-4o-mini-2024-07-18",
  "object": "chat.completion",
  "usage": {{
    "completion_tokens": 8,
    "prompt_tokens": 264,
    "total_tokens": 272
  }}
}}"""

            if streamRequested then
                do! respondChatCompletionSse ctx responseJson
            else
                do! respondJson ctx 200 responseJson
        }

    /// Fake Azure OpenAI audio/transcriptions handler (AlitaBot voice transcription).
    /// Doesn't parse the multipart body (the fake doesn't need the audio bytes) — just
    /// logs a placeholder call entry and returns the next scripted response, dequeued
    /// same as the other script queues. An empty queue returns an empty transcript.
    let handleAudioTranscriptions (ctx: HttpContext) =
        task {
            let url = ctx.Request.Path.ToString() + ctx.Request.QueryString.ToString()
            Console.WriteLine($"FAKE OPENAI IN {ctx.Request.Method} {url} (multipart audio, contentLength={ctx.Request.ContentLength})")
            Store.logCall ctx.Request.Method url $"(multipart audio, {ctx.Request.ContentLength} bytes)"

            let scripted =
                let mutable item = Unchecked.defaultof<ScriptedResponse>
                if Store.sttResponseScript.TryDequeue(&item) then Some item else None

            match scripted with
            | Some s ->
                if s.delayMs > 0 then do! Task.Delay(s.delayMs)
                match (if isNull (box s.errorMode) then "" else s.errorMode) with
                | "network" -> ctx.Abort()
                | "timeout" ->
                    do! Task.Delay(10_000)
                    do! respondJson ctx s.status s.body
                | _ -> do! respondJson ctx s.status s.body
            | None -> do! respondJson ctx 200 """{"text":""}"""
        }

    /// Fake Azure OpenAI audio/speech (TTS) handler (AlitaBot /say, Slice 9 stretch).
    /// Doesn't parse the JSON request body — just logs a call entry and returns the next
    /// scripted response, dequeued same as the other script queues. A scripted 200's
    /// `body` is treated as BASE64-ENCODED audio bytes (unlike the JSON-bodied endpoints
    /// elsewhere in this file) and written back as the raw binary response
    /// AzureFoundrySpeech.Synthesize expects (`sendBinaryWithRetry` reads the response body
    /// as raw bytes, not JSON); a non-200 scripted status is written as plain JSON/text
    /// error body, same convention as every other endpoint. An empty queue falls back to
    /// `Store.defaultTtsBytes`.
    let handleAudioSpeech (ctx: HttpContext) =
        task {
            let url = ctx.Request.Path.ToString() + ctx.Request.QueryString.ToString()
            let! body = readBody ctx
            Console.WriteLine($"FAKE OPENAI IN {ctx.Request.Method} {url} bodyLen={body.Length}")
            Store.logCall ctx.Request.Method url body

            let scripted =
                let mutable item = Unchecked.defaultof<ScriptedResponse>
                if Store.ttsResponseScript.TryDequeue(&item) then Some item else None

            let respondBinary (bytes: byte[]) =
                task {
                    ctx.Response.StatusCode <- 200
                    ctx.Response.ContentType <- "audio/ogg"
                    do! ctx.Response.Body.WriteAsync(bytes.AsMemory(0, bytes.Length))
                }

            match scripted with
            | Some s ->
                if s.delayMs > 0 then do! Task.Delay(s.delayMs)
                match (if isNull (box s.errorMode) then "" else s.errorMode) with
                | "network" -> ctx.Abort()
                | "timeout" ->
                    do! Task.Delay(10_000)
                    do! respondJson ctx s.status s.body
                | _ when s.status = 200 ->
                    let bytes = try Convert.FromBase64String s.body with _ -> Encoding.UTF8.GetBytes s.body
                    do! respondBinary bytes
                | _ -> do! respondJson ctx s.status s.body
            | None -> do! respondBinary Store.defaultTtsBytes
        }

    /// Fake Azure OpenAI images/generations handler (AlitaBot image generation, text->image).
    let handleImagesGenerations (ctx: HttpContext) =
        task {
            let url = ctx.Request.Path.ToString() + ctx.Request.QueryString.ToString()
            let! body = readBody ctx
            Console.WriteLine($"FAKE OPENAI IN {ctx.Request.Method} {url} bodyLen={body.Length}")
            Store.logCall ctx.Request.Method url body

            let scripted =
                let mutable item = Unchecked.defaultof<ScriptedResponse>
                if Store.imageResponseScript.TryDequeue(&item) then Some item else None

            match scripted with
            | Some s ->
                if s.delayMs > 0 then do! Task.Delay(s.delayMs)
                match (if isNull (box s.errorMode) then "" else s.errorMode) with
                | "network" -> ctx.Abort()
                | "timeout" ->
                    do! Task.Delay(10_000)
                    do! respondJson ctx s.status s.body
                | _ -> do! respondJson ctx s.status s.body
            | None -> do! respondJson ctx 200 Store.defaultImageResponse
        }

    /// Fake Azure OpenAI images/edits handler (AlitaBot image generation, img2img).
    /// Multipart request — reads the form so the source image's byte count can be logged
    /// (tests assert on it via GetAzureOcrCalls/Body, e.g. "imageBytes=123") without the
    /// fake needing to persist the actual image bytes anywhere.
    let handleImagesEdits (ctx: HttpContext) =
        task {
            let url = ctx.Request.Path.ToString() + ctx.Request.QueryString.ToString()
            let! form = ctx.Request.ReadFormAsync()
            let field (name: string) = if form.ContainsKey name then string form[name] else ""
            let promptField = field "prompt"
            let sizeField = field "size"
            let qualityField = field "quality"
            let imageByteCount =
                match form.Files.GetFile "image" with
                | null -> 0
                | f -> int f.Length
            let bodyLog =
                $"(multipart images/edits, prompt={promptField}, size={sizeField}, quality={qualityField}, imageBytes={imageByteCount})"
            Console.WriteLine($"FAKE OPENAI IN {ctx.Request.Method} {url} {bodyLog}")
            Store.logCall ctx.Request.Method url bodyLog

            let scripted =
                let mutable item = Unchecked.defaultof<ScriptedResponse>
                if Store.imageResponseScript.TryDequeue(&item) then Some item else None

            match scripted with
            | Some s ->
                if s.delayMs > 0 then do! Task.Delay(s.delayMs)
                match (if isNull (box s.errorMode) then "" else s.errorMode) with
                | "network" -> ctx.Abort()
                | "timeout" ->
                    do! Task.Delay(10_000)
                    do! respondJson ctx s.status s.body
                | _ -> do! respondJson ctx s.status s.body
            | None -> do! respondJson ctx 200 Store.defaultImageResponse
        }

    /// Builds `{"object":"list","data":[{"object":"embedding","index":i,"embedding":[...]}],"model":"fake-embedding","usage":{...}}`
    /// — one deterministic hash-of-text vector (Embedding.embed) per input. Kept as a
    /// plain (non-task) function: the nested `for` loops over 1536-dim vectors inside a
    /// `task { }` CE tripped the F# compiler's resumable-state-machine analysis (FS3511).
    let private buildEmbeddingsResponseBody (inputs: string list) : string =
        let root = JsonObject()
        root["object"] <- JsonValue.Create "list"
        let data = JsonArray()
        inputs
        |> List.iteri (fun i text ->
            let item = JsonObject()
            item["object"] <- JsonValue.Create "embedding"
            item["index"] <- JsonValue.Create i
            let arr = JsonArray()
            for v in Embedding.embed text do
                arr.Add(JsonValue.Create(float v))
            item["embedding"] <- arr
            data.Add item)
        root["data"] <- data
        root["model"] <- JsonValue.Create "fake-embedding"
        let usage = JsonObject()
        usage["prompt_tokens"] <- JsonValue.Create(inputs.Length * 5)
        usage["total_tokens"] <- JsonValue.Create(inputs.Length * 5)
        root["usage"] <- usage
        root.ToJsonString()

    /// Fake Azure OpenAI embeddings handler (AlitaBot's memory foundation, Slice 5a).
    /// Parses `input` (a string or an array of strings) and returns one deterministic
    /// hash-of-text vector per input (Embedding.embed — see its doc comment for the
    /// scheme), unless a scripted response is queued (SetAzureEmbeddingsScript), which
    /// takes priority — e.g. to inject a 429 and assert the embedding pipeline's
    /// failure-tolerance (Warning-logged, counted, never affects the reply path).
    let handleEmbeddings (ctx: HttpContext) =
        task {
            let url = ctx.Request.Path.ToString() + ctx.Request.QueryString.ToString()
            let! body = readBody ctx
            Console.WriteLine($"FAKE OPENAI IN {ctx.Request.Method} {url} bodyLen={body.Length}")
            Store.logCall ctx.Request.Method url body

            let scripted =
                let mutable item = Unchecked.defaultof<ScriptedResponse>
                if Store.embeddingsResponseScript.TryDequeue(&item) then Some item else None

            match scripted with
            | Some s ->
                if s.delayMs > 0 then do! Task.Delay(s.delayMs)
                match (if isNull (box s.errorMode) then "" else s.errorMode) with
                | "network" -> ctx.Abort()
                | "timeout" ->
                    do! Task.Delay(10_000)
                    do! respondJson ctx s.status s.body
                | _ -> do! respondJson ctx s.status s.body
            | None ->
                let inputs =
                    try
                        use doc = JsonDocument.Parse(body)
                        match doc.RootElement.TryGetProperty "input" with
                        | true, v when v.ValueKind = JsonValueKind.Array ->
                            [ for el in v.EnumerateArray() do
                                if el.ValueKind = JsonValueKind.String then
                                    el.GetString() |> Option.ofObj |> Option.defaultValue "" ]
                        | true, v when v.ValueKind = JsonValueKind.String ->
                            [ v.GetString() |> Option.ofObj |> Option.defaultValue "" ]
                        | _ -> []
                    with _ -> []

                do! respondJson ctx 200 (buildEmbeddingsResponseBody inputs)
        }

    /// Sets the scripted-response queue for the images/generations + images/edits endpoints
    /// (shared — one control endpoint for both, per plan). An empty/absent `responses` array
    /// clears it (calls fall back to the default scripted tiny PNG).
    let setImageScript (ctx: HttpContext) =
        task {
            let! body = readBody ctx
            try
                let payload =
                    JsonSerializer.Deserialize<ResponseScriptDto>(body, JsonSerializerOptions(JsonSerializerDefaults.Web))
                match payload with
                | null -> do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
                | payload ->
                    Store.clearImageScript ()
                    if not (isNull (box payload.responses)) then
                        for r in payload.responses do
                            Store.imageResponseScript.Enqueue r
                    do! respondJson ctx 200 """{"ok":true}"""
            with _ ->
                do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
        }

    /// Sets the scripted-response queue for the embeddings endpoint. An empty/absent
    /// `responses` array clears it (calls fall back to Embedding.embed's deterministic
    /// hash-of-text vectors).
    let setEmbeddingsScript (ctx: HttpContext) =
        task {
            let! body = readBody ctx
            try
                let payload =
                    JsonSerializer.Deserialize<ResponseScriptDto>(body, JsonSerializerOptions(JsonSerializerDefaults.Web))
                match payload with
                | null -> do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
                | payload ->
                    Store.clearEmbeddingsScript ()
                    if not (isNull (box payload.responses)) then
                        for r in payload.responses do
                            Store.embeddingsResponseScript.Enqueue r
                    do! respondJson ctx 200 """{"ok":true}"""
            with _ ->
                do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
        }

    /// Sets the scripted-response queue for the audio/transcriptions (STT) endpoint.
    /// An empty/absent `responses` array clears it (calls fall back to an empty transcript).
    let setSttScript (ctx: HttpContext) =
        task {
            let! body = readBody ctx
            try
                let payload =
                    JsonSerializer.Deserialize<ResponseScriptDto>(body, JsonSerializerOptions(JsonSerializerDefaults.Web))
                match payload with
                | null -> do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
                | payload ->
                    Store.clearSttScript ()
                    if not (isNull (box payload.responses)) then
                        for r in payload.responses do
                            Store.sttResponseScript.Enqueue r
                    do! respondJson ctx 200 """{"ok":true}"""
            with _ ->
                do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
        }

    /// Sets the scripted-response queue for the audio/speech (TTS) endpoint. An
    /// empty/absent `responses` array clears it (calls fall back to Store.defaultTtsBytes).
    let setTtsScript (ctx: HttpContext) =
        task {
            let! body = readBody ctx
            try
                let payload =
                    JsonSerializer.Deserialize<ResponseScriptDto>(body, JsonSerializerOptions(JsonSerializerDefaults.Web))
                match payload with
                | null -> do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
                | payload ->
                    Store.clearTtsScript ()
                    if not (isNull (box payload.responses)) then
                        for r in payload.responses do
                            Store.ttsResponseScript.Enqueue r
                    do! respondJson ctx 200 """{"ok":true}"""
            with _ ->
                do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
        }

    let getCalls (ctx: HttpContext) =
        task {
            let calls = Store.calls |> Seq.toArray
            let json = JsonSerializer.Serialize(calls, JsonSerializerOptions(JsonSerializerDefaults.Web))
            do! respondJson ctx 200 json
        }

    let clearCalls (ctx: HttpContext) =
        task {
            Store.clearCalls()
            do! respondJson ctx 200 """{"ok":true}"""
        }

    let resetMock (ctx: HttpContext) =
        task {
            Store.resetOcrMock()
            do! respondJson ctx 200 """{"ok":true}"""
        }

    let setResponse (ctx: HttpContext) =
        task {
            let! body = readBody ctx
            try
                let payload =
                    JsonSerializer.Deserialize<ResponseMockDto>(body, JsonSerializerOptions(JsonSerializerDefaults.Web))
                match payload with
                | null -> do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
                | payload ->
                    Store.responseStatus <- payload.status
                    Store.responseBody <- payload.body
                    do! respondJson ctx 200 """{"ok":true}"""
            with _ ->
                do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
        }

    let setDelay (ctx: HttpContext) =
        task {
            let! body = readBody ctx
            try
                let payload =
                    JsonSerializer.Deserialize<DelayMockDto>(body, JsonSerializerOptions(JsonSerializerDefaults.Web))
                match payload with
                | null -> do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
                | payload ->
                    Store.responseDelayMs <- payload.delayMs
                    do! respondJson ctx 200 """{"ok":true}"""
            with _ ->
                do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
        }

    let setErrorMode (ctx: HttpContext) =
        task {
            let! body = readBody ctx
            try
                let payload =
                    JsonSerializer.Deserialize<ErrorModeDto>(body, JsonSerializerOptions(JsonSerializerDefaults.Web))
                match payload with
                | null -> do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
                | payload ->
                    Store.responseErrorMode <- if isNull (box payload.mode) then "" else payload.mode
                    do! respondJson ctx 200 """{"ok":true}"""
            with _ ->
                do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
        }

    let setScript (ctx: HttpContext) =
        task {
            let! body = readBody ctx
            try
                let payload =
                    JsonSerializer.Deserialize<ResponseScriptDto>(body, JsonSerializerOptions(JsonSerializerDefaults.Web))
                match payload with
                | null -> do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
                | payload ->
                    Store.clearScript ()
                    if not (isNull (box payload.responses)) then
                        for r in payload.responses do
                            Store.responseScript.Enqueue r
                    do! respondJson ctx 200 """{"ok":true}"""
            with _ ->
                do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
        }

    /// Sets the scripted-response queue for the Azure OpenAI chat-completions endpoint.
    /// An empty/absent `responses` array clears it (calls fall back to keyword routing).
    let setLlmScript (ctx: HttpContext) =
        task {
            let! body = readBody ctx
            try
                let payload =
                    JsonSerializer.Deserialize<ResponseScriptDto>(body, JsonSerializerOptions(JsonSerializerDefaults.Web))
                match payload with
                | null -> do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
                | payload ->
                    Store.clearLlmScript ()
                    if not (isNull (box payload.responses)) then
                        for r in payload.responses do
                            Store.llmResponseScript.Enqueue r
                    do! respondJson ctx 200 """{"ok":true}"""
            with _ ->
                do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
        }

    /// Sets the chat-completions streaming knobs (chunk delay, mid-stream abort,
    /// Retry-After on scripted 429s). All zeros resets to defaults.
    let setLlmStreamOptions (ctx: HttpContext) =
        task {
            let! body = readBody ctx
            try
                let payload =
                    JsonSerializer.Deserialize<LlmStreamOptionsDto>(body, JsonSerializerOptions(JsonSerializerDefaults.Web))
                match payload with
                | null -> do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
                | payload ->
                    Store.llmStreamChunkDelayMs <- payload.chunkDelayMs
                    Store.llmStreamAbortAfterChunks <- payload.abortAfterChunks
                    Store.llmRetryAfterSeconds <- payload.retryAfterSeconds
                    do! respondJson ctx 200 """{"ok":true}"""
            with _ ->
                do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
        }

    /// Sets the scripted-response queue for the REACTION-triage chat-completions calls.
    /// An empty/absent `responses` array clears it (calls fall back to keyword routing).
    let setReactionLlmScript (ctx: HttpContext) =
        task {
            let! body = readBody ctx
            try
                let payload =
                    JsonSerializer.Deserialize<ResponseScriptDto>(body, JsonSerializerOptions(JsonSerializerDefaults.Web))
                match payload with
                | null -> do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
                | payload ->
                    Store.clearReactionLlmScript ()
                    if not (isNull (box payload.responses)) then
                        for r in payload.responses do
                            Store.reactionLlmResponseScript.Enqueue r
                    do! respondJson ctx 200 """{"ok":true}"""
            with _ ->
                do! respondJson ctx (int HttpStatusCode.BadRequest) """{"ok":false}"""
        }

