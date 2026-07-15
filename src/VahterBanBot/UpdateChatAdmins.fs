module VahterBanBot.UpdateChatAdmins

open System.Collections.Generic
open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open VahterBanBot.Types
open VahterBanBot.Utils
open BotInfra
open System
open System.Threading
open Microsoft.Extensions.Hosting

module Req = Funogram.Telegram.Req

type UpdateChatAdmins(
    logger: ILogger<UpdateChatAdmins>,
    tg: ITelegramApi,
    botConf: IOptions<BotConfiguration>
) =
    let mutable timer: Timer = null
    static let mutable localAdmins: ISet<int64> = HashSet<int64>()

    let updateChatAdmins _ = task {
        try
            let sb = StringBuilder()
            %sb.AppendLine("New chat admins:")
            let result = HashSet<int64>()
            for chatId in botConf.Value.ChatsToMonitor.Values do
                let! admins = tg.CallExn(Req.GetChatAdministrators.Make chatId)

                // wait a bit so we don't get rate limited
                do! Task.Delay 100

                for admin in admins do
                    // status/user read via Tg helpers — the ChatMember DU cases are
                    // misdiscriminated by Funogram's converter (see Utils.Tg).
                    let user = Tg.chatMemberUser admin
                    if result.Add user.Id then
                        %sb.AppendJoin(",", $"{prependUsername (Option.toObj user.Username)} ({user.Id})")
            UpdateChatAdmins.Admins <- result
            logger.LogInformation (sb.ToString())
        with
        | ex -> logger.LogError(ex, "Error while updating chat admins.")
    }

    static member Admins
        with get() = Volatile.Read &localAdmins
        and private set(value: ISet<int64>) =
            Volatile.Write(&localAdmins, value)

    interface IHostedService with
        member this.StartAsync _ =
            if not botConf.Value.IgnoreSideEffects && botConf.Value.UpdateChatAdmins then
                if botConf.Value.UpdateChatAdminsInterval.IsSome then
                    // recurring
                    timer <- new Timer(TimerCallback(updateChatAdmins >> ignore), null, TimeSpan.Zero, botConf.Value.UpdateChatAdminsInterval.Value)
                    Task.CompletedTask
                else
                    // once
                    updateChatAdmins()
            else
                Task.CompletedTask

        member this.StopAsync _ =
            match timer with
            | null -> Task.CompletedTask
            | timer -> timer.DisposeAsync().AsTask()
