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
    botConf: IOptions<BotConfiguration>,
    db: DbService
) =
    let mutable timer: Timer = null
    let podId = getEnvOr "POD_NAME" Environment.MachineName
    static let mutable localAdmins: ISet<int64> = HashSet<int64>()

    /// Every tick, every pod reloads the local snapshot and attempts the Telegram fetch under the
    /// 'chat_admins_refresh' lease, so only one pod calls GetChatAdministrators per window.
    let refreshChatAdmins _ = task {
        try
            let minInterval = botConf.Value.UpdateChatAdminsInterval |> Option.defaultValue (TimeSpan.FromDays 3650.0)
            let! acquired = db.TryAcquireIntervalJob("chat_admins_refresh", minInterval, podId)
            if acquired then
                try
                    let sb = StringBuilder()
                    %sb.AppendLine("New chat admins:")
                    let fetched = ResizeArray<int64 * int64>()
                    for chatId in botConf.Value.ChatsToMonitor.Values do
                        let! admins = tg.CallExn(Req.GetChatAdministrators.Make chatId)

                        // wait a bit so we don't get rate limited
                        do! Task.Delay 100

                        for admin in admins do
                            let user = Tg.chatMemberUser admin
                            fetched.Add(chatId, user.Id)
                            %sb.AppendJoin(",", $"{prependUsername (Option.toObj user.Username)} ({user.Id})")
                    do! db.SaveChatAdmins(fetched.ToArray())
                    do! db.CompleteScheduledJob "chat_admins_refresh"
                    logger.LogInformation (sb.ToString())
                with ex ->
                    logger.LogError(ex, "Error fetching chat admins from Telegram.")
            // Always reload the local snapshot — whether this pod just fetched or another pod did.
            let! admins = db.GetChatAdminIds()
            UpdateChatAdmins.Admins <- HashSet admins
        with ex ->
            logger.LogError(ex, "Error while refreshing chat admins snapshot.")
    }

    static member Admins
        with get() = Volatile.Read &localAdmins
        and private set(value: ISet<int64>) =
            Volatile.Write(&localAdmins, value)

    interface IHostedService with
        member this.StartAsync _ =
            if not botConf.Value.IgnoreSideEffects && botConf.Value.UpdateChatAdmins then
                // Interval None (one-shot config) still runs a recurring local timer — the lease's
                // minInterval above is what makes the Telegram fetch itself effectively one-shot.
                let interval = botConf.Value.UpdateChatAdminsInterval |> Option.defaultValue (TimeSpan.FromMinutes 5.0)
                timer <- new Timer(TimerCallback(refreshChatAdmins >> ignore), null, TimeSpan.Zero, interval)
            Task.CompletedTask

        member this.StopAsync _ =
            match timer with
            | null -> Task.CompletedTask
            | timer -> timer.DisposeAsync().AsTask()
