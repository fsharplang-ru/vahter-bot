module VahterBanBot.FakeTgApi

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Funogram.Telegram.Types
open VahterBanBot.Types
open VahterBanBot.Utils

let fakeTgApi (botConf: BotConfiguration) =
    { new DelegatingHandler() with
        member x.SendAsync(request, cancellationToken) =
            let apiResult text =
                let resp = new HttpResponseMessage(HttpStatusCode.OK)
                resp.Content <- new StringContent($"""{{"ok":true,"result":{text}}}""", Encoding.UTF8, "application/json")
                resp

            let url = request.RequestUri.ToString()
            let resp =
                if not(url.StartsWith("https://api.telegram.org/bot" + botConf.BotToken)) then
                   // return 404 for any other request
                   new HttpResponseMessage(HttpStatusCode.NotFound)
                elif url.EndsWith "/deleteMessage" || url.EndsWith "/banChatMember" then
                    // respond with "true"
                   apiResult "true"
                elif url.EndsWith "/sendMessage" then
                    // respond with the request body as a string
                    let message =
                        Message.Create(
                            messageId = 1,
                            date = DateTime.UtcNow,
                            chat = Chat.Create(
                                id = 1L,
                                ``type`` = ChatType.Private
                            )
                        )
                        |> fun x -> JsonSerializer.Serialize(x, options = jsonOptions)
                    apiResult message
                elif url.EndsWith "/getChatAdministrators" then
                    // respond with the request body as a string
                    let message =
                        [|
                            ChatMemberAdministrator.Create(
                                status = "",
                                canDeleteStories = false,
                                canEditStories = false,
                                canPostStories = false,
                                canInviteUsers = false,
                                canPromoteMembers = false,
                                canRestrictMembers = false,
                                canChangeInfo = false,
                                canDeleteMessages = false,
                                canManageChat = false,
                                isAnonymous = false,
                                canBeEdited = false,
                                canManageVideoChats = false,
                                user = User.Create(
                                    id = 42L,
                                    isBot = false,
                                    firstName = "just_admin",
                                    username = "just_admin"
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
