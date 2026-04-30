namespace FakeAzureOcrApi

open System
open System.Net
open System.Text
open System.Text.Json
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

            do! respondJson ctx Store.responseStatus Store.responseBody
        }

    /// Fake Azure OpenAI Chat Completions handler.
    /// Routes verdict based on keywords in the user message:
    ///   "kill"  → SPAM,  "spam"  → SKIP,  otherwise → NOT_SPAM
    let handleChatCompletions (ctx: HttpContext) =
        task {
            let url = ctx.Request.Path.ToString() + ctx.Request.QueryString.ToString()
            let! body = readBody ctx
            Console.WriteLine($"FAKE OPENAI IN {ctx.Request.Method} {url} bodyLen={body.Length}")
            Store.logCall ctx.Request.Method url body

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

            let responseJson =
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

            do! respondJson ctx 200 responseJson
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

