module VahterBanBot.FakeTgApi

open System
open System.Net
open System.Net.Http
open System.Text
open Newtonsoft.Json
open Telegram.Bot.Types
open Telegram.Bot.Types.Enums
open VahterBanBot.Types

let fakeTgApi (botConf: BotConfiguration) =
    { new DelegatingHandler() with
        member x.SendAsync(request, cancellationToken) = task {
            let apiResult text =
                let resp = new HttpResponseMessage(HttpStatusCode.OK)
                resp.Content <- new StringContent($"""{{"Ok":true,"Result":{text}}}""", Encoding.UTF8, "application/json")
                resp
                
            let url = request.RequestUri.ToString()
            if not(url.StartsWith("https://api.telegram.org/bot" + botConf.BotToken)) then
               // return 404 for any other request
               return new HttpResponseMessage(HttpStatusCode.NotFound)
            elif url.EndsWith "/deleteMessage" || url.EndsWith "/banChatMember" then
                // respond with "true"
                return apiResult "true"
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
                    |> JsonConvert.SerializeObject
                return apiResult message
            else
                // return 500 for any other request
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
        }
    }
