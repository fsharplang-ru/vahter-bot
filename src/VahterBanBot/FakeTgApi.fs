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

let fakeTgApi (botConf: BotConfiguration) =
    { new DelegatingHandler() with
        member x.SendAsync(request, cancellationToken) =
            let apiResult text =
                let resp = new HttpResponseMessage(HttpStatusCode.OK)
                resp.Content <- new StringContent($"""{{"ok":true,"result":{text}}}""", Encoding.UTF8, "application/json")
                resp

            let parseQueryValue (key: string) =
                request.RequestUri.Query.TrimStart('?').Split('&')
                |> Array.choose (fun part ->
                    let kv = part.Split('=')
                    if kv.Length = 2 && kv[0] = key then Some(Uri.UnescapeDataString kv[1]) else None)
                |> Array.tryHead

            let url = request.RequestUri.ToString()
            let resp =
                if not(url.StartsWith("https://api.telegram.org/bot" + botConf.BotToken))
                   && not(url.StartsWith("https://api.telegram.org/file/bot" + botConf.BotToken)) then
                   // return 404 for any other request
                   new HttpResponseMessage(HttpStatusCode.NotFound)
                elif url.Contains "/getFile" then
                    let fileId = parseQueryValue "file_id" |> Option.defaultValue "file_id"
                    let filePath = $"photos/{fileId}.jpg"
                    apiResult $"""{{"file_id":"{fileId}","file_unique_id":"{fileId}-unique","file_size":1024,"file_path":"{filePath}"}}"""
                elif url.Contains "/file/bot" then
                    let content = Encoding.UTF8.GetBytes(url)
                    let resp = new HttpResponseMessage(HttpStatusCode.OK)
                    resp.Content <- new ByteArrayContent(content)
                    resp.Content.Headers.ContentType <- MediaTypeHeaderValue("application/octet-stream")
                    resp
                elif url.EndsWith "/deleteMessage" || url.EndsWith "/banChatMember" then
                    // respond with "true"
                   apiResult "true"
                elif url.EndsWith "/sendMessage" then
                    // respond with the request body as a string
                    let message =
                        Message(
                            MessageId = 1,
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
            Task.FromResult resp
    }

let fakeOcrApi (botConf: BotConfiguration) =
    { new DelegatingHandler() with
        member _.SendAsync(request, cancellationToken) =
            task {
                let url = request.RequestUri.ToString()

                if botConf.AzureOcrEndpoint <> "" && url.StartsWith(botConf.AzureOcrEndpoint) then
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
    }
