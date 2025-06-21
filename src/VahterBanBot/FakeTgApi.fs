module VahterBanBot.FakeTgApi

open System
open System.Net
open System.Net.Http
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
                    
                elif url.EndsWith("/getChat") then
                    let message =
                        ChatFullInfo()
                    apiResult message
                    
                else
                    // return 500 for any other request
                    // TODO pass fucking ILogger here somehow -_-
                    Console.WriteLine $"Unhandled request: {url}"
                    new HttpResponseMessage(HttpStatusCode.InternalServerError)
            Task.FromResult resp
    }
