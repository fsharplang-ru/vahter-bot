module VahterBanBot.BotCommandsSetup

open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Funogram.Telegram.Types
open VahterBanBot.Types
open BotInfra

/// At startup, registers the public-chat moderation commands (/ban, /sban, /unban) in Telegram.
/// With EphemeralCommandsEnabled they are marked is_ephemeral (Bot API 10.2), so clients send
/// them invisibly to other chat members. When the flag is off, the group-scope registration is
/// removed so clients fall back to plain visible commands (the send-then-delete flow still
/// covers those, as it does for old clients that ignore is_ephemeral).
type BotCommandsSetupService(
    tg: ITelegramApi,
    botConf: IOptions<BotConfiguration>,
    logger: ILogger<BotCommandsSetupService>
) =
    interface IHostedService with
        member _.StartAsync _ =
            task {
                let cfg = botConf.Value
                if not cfg.IgnoreSideEffects then
                    try
                        let scope = BotCommandScope.AllGroupChats(BotCommandScopeAllGroupChats.Create("all_group_chats"))
                        if cfg.EphemeralCommandsEnabled then
                            let commands =
                                [| BotCommand.Create("ban", "Забанить (ответом на сообщение спамера)", isEphemeral = true)
                                   BotCommand.Create("sban", "Софтбан (ответом на сообщение спамера)", isEphemeral = true)
                                   BotCommand.Create("unban", "Разбанить юзера по ID", isEphemeral = true) |]
                            do! tg.CallExn(Funogram.Telegram.Req.SetMyCommands.Make(commands, scope = scope)) |> taskIgnore
                            logger.LogInformation "Registered ephemeral group commands (/ban, /sban, /unban)"
                        else
                            do! tg.CallExn(Funogram.Telegram.Req.DeleteMyCommands.Make(scope = scope)) |> taskIgnore
                            logger.LogInformation "Ephemeral commands disabled; removed group-scope command registration"
                    with ex ->
                        logger.LogWarning(ex, "Could not update bot command registration in Telegram")
            }
            :> Task

        member _.StopAsync _ = Task.CompletedTask
