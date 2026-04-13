namespace FakeTgApi

open System
open System.Net
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http

module Handlers =
    let okResult (resultJson: string) =
        $"""{{"ok":true,"result":{resultJson}}}"""

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

    let methodFromPath (path: string) =
        // /bot{token}/{method}
        let parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
        if parts.Length >= 2 && parts[0].StartsWith("bot", StringComparison.OrdinalIgnoreCase) then
            Some parts[1]
        else None

    // ── JSON parsing helpers ─────────────────────────────────────────────────
    let parseChatId (body: string) : int64 =
        try
            use doc = JsonDocument.Parse(body: string)
            match doc.RootElement.TryGetProperty("chat_id") with
            | true, v -> v.GetInt64()
            | _ -> 1L
        with _ -> 1L

    let parseChatIdAndMessageId (body: string) =
        let chatId = parseChatId body
        let messageId =
            try
                use doc = JsonDocument.Parse(body: string)
                match doc.RootElement.TryGetProperty("message_id") with
                | true, v -> v.GetInt32()
                | _ -> 1
            with _ -> 1
        chatId, messageId

    // ── Aux functions for each Telegram method ───────────────────────────────
    // Extracted to keep handleTelegramMethod's task{} small (avoids FS3511).

    let private handleSimpleOk ctx =
        respondJson ctx 200 (okResult "true")

    let private handleSimulatedError ctx =
        respondJson ctx 400 """{"ok":false,"error_code":400,"description":"Simulated error by test"}"""

    let private handleSendMessage ctx body =
        let chatId = parseChatId body
        let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        let resultJson =
            $"""{{"message_id":1,"date":{now},"chat":{{"id":{chatId},"type":"private"}},"text":"ok"}}"""
        respondJson ctx 200 (okResult resultJson)

    let private handleSendPhoto ctx body =
        let chatId = parseChatId body
        let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        let resultJson =
            $"""{{"message_id":1,"date":{now},"chat":{{"id":{chatId},"type":"private"}},"caption":"ok"}}"""
        respondJson ctx 200 (okResult resultJson)

    let private handleSendMediaGroup ctx body =
        let chatId = parseChatId body
        let count =
            try
                use doc = JsonDocument.Parse(body)
                let media = doc.RootElement.GetProperty("media")
                media.EnumerateArray() |> Seq.length
            with _ -> 1
        let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        let msgs =
            [| for i in 1 .. count do
                   $"""{{"message_id":{i},"date":{now},"chat":{{"id":{chatId},"type":"private"}}}}""" |]
            |> String.concat ","
        respondJson ctx 200 (okResult $"[{msgs}]")

    let private handleForwardMessage ctx body =
        let chatId = parseChatId body
        let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        let resultJson =
            $"""{{"message_id":1,"date":{now},"chat":{{"id":{chatId},"type":"private"}}}}"""
        respondJson ctx 200 (okResult resultJson)

    let private handleGetChatMember ctx (body: string) =
        let userId =
            try
                use doc = JsonDocument.Parse(body)
                doc.RootElement.GetProperty("user_id").GetInt64()
            with _ -> 0L
        let status =
            match Store.chatMemberStatus.TryGetValue userId with
            | true, s -> s
            | _ -> "member"
        let normalized =
            match status with
            | "kicked" | "left" | "administrator" | "creator" as s -> s
            | _ -> "member"
        let resultJson =
            $"""{{"status":"{normalized}","user":{{"id":{userId},"is_bot":false,"first_name":"x"}}}}"""
        respondJson ctx 200 (okResult resultJson)

    let private handleGetFile ctx (body: string) =
        let fileId =
            try
                use doc = JsonDocument.Parse(body)
                doc.RootElement.GetProperty("file_id").GetString()
            with _ -> "file"
        let filePath = $"photos/{fileId}.jpg"
        let resultJson =
            $"""{{"file_id":"{fileId}","file_unique_id":"{fileId}-uid","file_size":1024,"file_path":"{filePath}"}}"""
        respondJson ctx 200 (okResult resultJson)

    let private handleGetChatAdministrators ctx =
        let resultJson =
            """[{"status":"administrator","can_be_edited":false,"is_anonymous":false,"can_delete_messages":false,"can_manage_video_chats":false,"can_restrict_members":false,"can_promote_members":false,"can_change_info":false,"can_invite_users":false,"user":{"id":42,"is_bot":false,"first_name":"just_admin","username":"just_admin"}}]"""
        respondJson ctx 200 (okResult resultJson)

    let private handleMessageWithChatAndId ctx body =
        let chatId, messageId = parseChatIdAndMessageId body
        let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        let resultJson =
            $"""{{"message_id":{messageId},"date":{now},"chat":{{"id":{chatId},"type":"private"}},"text":"ok"}}"""
        respondJson ctx 200 (okResult resultJson)

    // ── Main dispatcher ──────────────────────────────────────────────────────

    let private handleAuxMethod ctx (methodName: string) (body: string) (url: string)= task {
        let len = if isNull body then 0 else body.Length
        Console.WriteLine($"FAKE TG IN  {methodName} {url} bodyLen={len}")
        Store.logCall methodName url body

        match methodName with
        | m when Store.methodErrors.TryGetValue(m) |> fst ->
            do! handleSimulatedError ctx
        | "sendMessage"      -> do! handleSendMessage ctx body
        | "sendPhoto"        -> do! handleSendPhoto ctx body
        | "sendMediaGroup"   -> do! handleSendMediaGroup ctx body
        | "forwardMessage"   -> do! handleForwardMessage ctx body
        | "answerCallbackQuery" | "deleteMessage"
        | "banChatMember" | "unbanChatMember" | "restrictChatMember" ->
            do! handleSimpleOk ctx
        | "getChatMember"    -> do! handleGetChatMember ctx body
        | "getFile"          -> do! handleGetFile ctx body
        | "getChatAdministrators" -> do! handleGetChatAdministrators ctx
        | "editMessageReplyMarkup" | "editMessageText" ->
            do! handleMessageWithChatAndId ctx body
        | _ -> do! handleSimpleOk ctx
    }
    
    let handleTelegramMethod (ctx: HttpContext) =
        task {
            let! body = readBody ctx
            let url = ctx.Request.Path.ToString()
            match methodFromPath url with
            | None ->
                do! respondJson ctx (int HttpStatusCode.NotFound) """{"ok":false}"""
            | Some methodName ->
                do! handleAuxMethod ctx methodName body url
        }

    let handleFileDownload (ctx: HttpContext) =
        task {
            // Expected: /file/bot{token}/photos/{fileId}.jpg
            let path = ctx.Request.Path.ToString()
            let fileName = IO.Path.GetFileName(path)
            let fileId =
                if String.IsNullOrWhiteSpace fileName then null
                else
                    // "photos/<fileId>.jpg" (we keep whatever before first '.')
                    let dot = fileName.IndexOf('.')
                    if dot > 0 then fileName.Substring(0, dot) else fileName

            let bytes =
                if not (String.IsNullOrWhiteSpace fileId) then
                    match Store.files.TryGetValue fileId with
                    | true, b -> b
                    | _ -> Encoding.UTF8.GetBytes(path)
                else
                    Encoding.UTF8.GetBytes(path)

            ctx.Response.StatusCode <- 200
            ctx.Response.ContentType <- "application/octet-stream"
            do! ctx.Response.Body.WriteAsync(bytes.AsMemory(0, bytes.Length))
        }

    let getCalls (ctx: HttpContext) =
        task {
            let methodFilter =
                if ctx.Request.Query.ContainsKey("method") then
                    string ctx.Request.Query["method"]
                else null

            let calls =
                Store.calls
                |> Seq.filter (fun c -> isNull methodFilter || c.Method = methodFilter)
                |> Seq.toArray

            let json = JsonSerializer.Serialize(calls, JsonSerializerOptions(JsonSerializerDefaults.Web))
            do! respondJson ctx 200 json
        }

    let clearCalls (ctx: HttpContext) =
        task {
            Store.clearCalls()
            do! respondJson ctx 200 (okResult "true")
        }

    let setChatMember (ctx: HttpContext) =
        task {
            let! body = readBody ctx
            try
                let payload = JsonSerializer.Deserialize<ChatMemberMock>(body, JsonSerializerOptions(JsonSerializerDefaults.Web))
                Store.chatMemberStatus[payload.userId] <- payload.status
                do! respondJson ctx 200 (okResult "true")
            with _ ->
                do! respondJson ctx 400 (okResult "false")
        }

    let setFile (ctx: HttpContext) =
        task {
            let! body = readBody ctx
            try
                let payload = JsonSerializer.Deserialize<FileMock>(body, JsonSerializerOptions(JsonSerializerDefaults.Web))
                if Object.ReferenceEquals(payload, null) || String.IsNullOrWhiteSpace payload.fileId then
                    do! respondJson ctx 400 (okResult "false")
                else
                    let bytes = Convert.FromBase64String(payload.contentBase64)
                    Store.files[payload.fileId] <- bytes
                    do! respondJson ctx 200 (okResult "true")
            with _ ->
                do! respondJson ctx 400 (okResult "false")
        }

    let setMethodError (ctx: HttpContext) =
        task {
            let! body = readBody ctx
            try
                let payload = JsonSerializer.Deserialize<MethodErrorMock>(body, JsonSerializerOptions(JsonSerializerDefaults.Web))
                if Object.ReferenceEquals(payload, null) || String.IsNullOrWhiteSpace payload.methodName then
                    do! respondJson ctx 400 (okResult "false")
                elif payload.enabled then
                    Store.methodErrors[payload.methodName] <- true
                    do! respondJson ctx 200 (okResult "true")
                else
                    Store.methodErrors.TryRemove(payload.methodName) |> ignore
                    do! respondJson ctx 200 (okResult "true")
            with _ ->
                do! respondJson ctx 400 (okResult "false")
        }
