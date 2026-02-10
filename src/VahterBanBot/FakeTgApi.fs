module VahterBanBot.FakeTgApi

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Telegram.Bot.Types
open Telegram.Bot.Types.Enums
open VahterBanBot.Types
open VahterBanBot.Utils

let tryParseFileIdFromBodyValue(request: HttpRequestMessage) =
    let mutable fileId: string option = None
    if request.Method = HttpMethod.Post && not(isNull request.Content) then
        let body = request.Content.ReadAsStringAsync().Result
        let jsonDoc = JsonDocument.Parse(body)
        match jsonDoc.RootElement.TryGetProperty("file_id") with
        | true, parsedFileId ->
            fileId <- Some (parsedFileId.GetString())
        | _ -> ()
    fileId

let fakeTgApi (botConf: BotConfiguration) (request: HttpRequestMessage) =
    let apiResult text =
        let resp = new HttpResponseMessage(HttpStatusCode.OK)
        resp.Content <- new StringContent($"""{{"ok":true,"result":{text}}}""", Encoding.UTF8, "application/json")
        resp

    let url = request.RequestUri.ToString()
    let resp =
        if not(url.StartsWith("https://api.telegram.org/bot" + botConf.BotToken))
           && not(url.StartsWith("https://api.telegram.org/file/bot" + botConf.BotToken)) then
           // return 404 for any other request
           new HttpResponseMessage(HttpStatusCode.NotFound)
        elif url.Contains "/getFile" then
            tryParseFileIdFromBodyValue request
            |> Option.map (fun fileId ->
                let filePath = $"photos/{fileId}.jpg"
                apiResult $"""{{"file_id":"{fileId}","file_unique_id":"{fileId}-unique","file_size":1024,"file_path":"{filePath}"}}"""
            )
            |> Option.defaultWith (fun () ->
                new HttpResponseMessage(HttpStatusCode.InternalServerError)
            )

        elif url.Contains "/file/bot" then
            let content = Encoding.UTF8.GetBytes(url)
            let resp = new HttpResponseMessage(HttpStatusCode.OK)
            resp.Content <- new ByteArrayContent(content)
            resp.Content.Headers.ContentType <- MediaTypeHeaderValue("application/octet-stream")
            resp
        elif url.EndsWith "/deleteMessage" || url.EndsWith "/banChatMember" || url.EndsWith "/unbanChatMember" then
            // respond with "true"
           apiResult "true"
        elif url.EndsWith "/sendMessage" then
            // respond with the request body as a string
            let message =
                Message(
                    Id = 1,
                    Date = DateTime.UtcNow,
                    Chat = Chat(
                        Id = 1L,
                        Type = ChatType.Private
                    )
                )
                |> fun x -> JsonSerializer.Serialize(x, options = jsonOptions)
            apiResult message
        elif url.EndsWith "/getChatAdministrators" then
            // respond with the request body as a string
            let message =
                [|
                    ChatMemberAdministrator(
                        CanBeEdited = false,
                        IsAnonymous = false,
                        CanDeleteMessages = false,
                        CanManageVideoChats = false,
                        CanRestrictMembers = false,
                        CanPromoteMembers = false,
                        CanChangeInfo = false,
                        CanInviteUsers = false,
                        User = User(
                            Id = 42L,
                            FirstName = "just_admin",
                            Username = "just_admin"
                        )
                    )
                |]
                |> fun x -> JsonSerializer.Serialize(x, options = jsonOptions)
            apiResult message
        else
            // return 500 for any other request
            // TODO pass fucking ILogger here somehow -_-
            Console.WriteLine $"Unhandled request: {url}"
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
    resp

let fakeOcrApi (botConf: BotConfiguration) (request: HttpRequestMessage)=
    task {
        let url = request.RequestUri.ToString()

        if botConf.AzureOcrEndpoint <> "" && url.StartsWith(botConf.AzureOcrEndpoint) then
            if request.Method <> HttpMethod.Post then
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
            elif isNull request.Content then
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
            elif not(request.Headers.Contains("Ocp-Apim-Subscription-Key")) then
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            elif request.Headers.GetValues("Ocp-Apim-Subscription-Key")
                 |> Seq.head <> botConf.AzureOcrKey then
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
            else

            let! payload =
                if isNull request.Content then
                    Task.FromResult String.Empty
                else
                    request.Content.ReadAsStringAsync()

            let detectedFileId =
                let marker = "/photos/"
                let idx = payload.IndexOf(marker, StringComparison.OrdinalIgnoreCase)
                if idx >= 0 then
                    let remainder = payload.Substring(idx + marker.Length)
                    remainder.Split([|'/' ; '.'|], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.tryHead
                else
                    None

            let text =
                match detectedFileId with
                | Some "spam" -> "2222222"
                | _ -> "b"

            let sampleAzureResponse =
                $"""{{
  "modelVersion": "2023-10-01",
  "metadata": {{
    "width": 1020,
    "height": 638
  }},
  "readResult": {{
    "blocks": [
      {{
        "lines": [
          {{
            "text": "{text}",
            "boundingPolygon": [
              {{ "x": 1, "y": 24 }},
              {{ "x": 1005, "y": 27 }},
              {{ "x": 1004, "y": 377 }},
              {{ "x": 0, "y": 371 }}
            ],
            "words": [
              {{ "text": "{text}", "confidence": 0.9 }}
            ]
          }}
        ]
      }}
    ]
  }}
}}"""

            let r = new HttpResponseMessage(HttpStatusCode.OK)
            r.Content <- new StringContent(sampleAzureResponse, Encoding.UTF8, "application/json")
            return r
        else
            return new HttpResponseMessage(HttpStatusCode.NotFound)
    }

let fakeApi (botConf: BotConfiguration) =
    { new DelegatingHandler() with
        member x.SendAsync(request, cancellationToken) = task {
            let url = request.RequestUri.ToString()
            if url.StartsWith("https://api.telegram.org") then
                return fakeTgApi botConf request
            elif url.StartsWith(botConf.AzureOcrEndpoint) then
                return! fakeOcrApi botConf request
            else
                return new HttpResponseMessage(HttpStatusCode.NotFound)
        }
    }
