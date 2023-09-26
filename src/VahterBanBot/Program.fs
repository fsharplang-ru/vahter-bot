open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open Giraffe
open Microsoft.Extensions.DependencyInjection
open VahterBanBot.Utils

[<CLIMutable>]
type BotConfiguration =
    { BotToken: string
      Route: string
      SecretToken: string }

let botConf =
    { BotToken = getEnv "BOT_TELEGRAM_TOKEN"
      Route = getEnvOr "BOT_HOOK_ROUTE" "/bot"
      SecretToken = getEnv "BOT_AUTH_TOKEN" }

let validateApiKey (ctx : HttpContext) =
    match ctx.TryGetRequestHeader "X-Telegram-Bot-Api-Secret-Token" with
    | Some key when key = botConf.SecretToken -> true
    | _ -> false

let accessDenied = setStatusCode 401 >=> text "Access Denied"
let requiresApiKey = authorizeRequest validateApiKey accessDenied

let builder = WebApplication.CreateBuilder()
%builder.Services
    .AddSingleton(botConf)
    .AddGiraffe()
    .AddHttpClient("telegram_bot_client")
    .AddTypedClient(fun httpClient sp ->
        let options = TelegramBotClientOptions(botConf.BotToken)
        TelegramBotClient(options, httpClient) :> ITelegramBotClient)

let app = builder.Build()

let webApp = choose [
    POST >=> route botConf.Route >=> requiresApiKey >=> bindJson<Update> (fun update next ctx -> task {
        use scope = ctx.RequestServices.CreateScope()
        let telegramClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();
        
        if update.Message <> null then
            let! _ = telegramClient.SendTextMessageAsync(ChatId(update.Message.Chat.Id), $"Received {update.Message.Text}") in ()
        
        return! Successful.OK() next ctx
    })
    GET >=> route "/" >=> text "Hello World!"
]

app.UseGiraffe(webApp)
app.Run()
