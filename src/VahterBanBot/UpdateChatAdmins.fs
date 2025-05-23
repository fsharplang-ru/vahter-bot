﻿module VahterBanBot.UpdateChatAdmins

open System.Collections.Generic
open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Telegram.Bot
open Telegram.Bot.Types
open VahterBanBot.Types
open VahterBanBot.Utils
open System
open System.Threading
open Microsoft.Extensions.Hosting

type UpdateChatAdmins(
    logger: ILogger<UpdateChatAdmins>,
    telegramClient: ITelegramBotClient,
    botConf: BotConfiguration
) =
    let mutable timer: Timer = null
    static let mutable localAdmins: ISet<int64> = HashSet<int64>()

    let updateChatAdmins _ = task {
        try
            let sb = StringBuilder()
            %sb.AppendLine("New chat admins:")
            let result = HashSet<int64>()
            for chatId in botConf.ChatsToMonitor.Values do
                let! admins = telegramClient.GetChatAdministratorsAsync(ChatId chatId)

                // wait a bit so we don't get rate limited
                do! Task.Delay 100

                for admin in admins do
                    if result.Add admin.User.Id then
                        %sb.AppendJoin(",", $"{prependUsername admin.User.Username} ({admin.User.Id})")
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
            if not botConf.IgnoreSideEffects && botConf.UpdateChatAdmins then
                if botConf.UpdateChatAdminsInterval.IsSome then
                    // recurring
                    timer <- new Timer(TimerCallback(updateChatAdmins >> ignore), null, TimeSpan.Zero, botConf.UpdateChatAdminsInterval.Value)
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
