module VahterBanBot.BotCommandsSetup

open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Funogram.Telegram.Types
open VahterBanBot.Types
open BotInfra

/// At startup, registers (or removes) Telegram's command-menu autocomplete entry for
/// /vahter_report — and ONLY /vahter_report. /ban, /sban, /unban are deliberately NEVER
/// registered, in any scope: registration is pure autocomplete cosmetics (execution is
/// text-parsing + isUserVahter, unaffected by whether a command is registered at all), and
/// `is_ephemeral` on a registered BotCommand does NOT affect menu visibility — it only hides
/// the sender's own command-message bubble after they send it. With scope AllGroupChats, a
/// registered command is visible in EVERY chat member's menu regardless of is_ephemeral. That
/// was proven in prod: /ban, /sban, /unban registered as ephemeral under
/// EPHEMERAL_COMMANDS_ENABLED still showed up in ordinary (non-vahter) members' menus, and the
/// flag was turned back off 11 minutes after first being enabled. Advertising admin-only
/// commands to the whole chat is undesirable regardless of is_ephemeral, so they stay
/// unregistered permanently — /vahter_report is the only command safe to advertise, since any
/// chat member is allowed to use it.
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
                        if cfg.ReportCommandEnabled then
                            let commands =
                                // isEphemeral = true still hides the invoking member's own command
                                // bubble from the rest of the chat, even though it doesn't affect
                                // who sees the menu entry itself (see the type doc comment above).
                                [| BotCommand.Create("vahter_report", "Отчёт: сколько спама поймал бот (виден только вам)", isEphemeral = true) |]
                            do! tg.CallExn(Funogram.Telegram.Req.SetMyCommands.Make(commands, scope = scope)) |> taskIgnore
                            logger.LogInformation "Registered /vahter_report as the only group-scope command"
                        else
                            do! tg.CallExn(Funogram.Telegram.Req.DeleteMyCommands.Make(scope = scope)) |> taskIgnore
                            logger.LogInformation "Report command disabled; removed group-scope command registration"
                    with ex ->
                        logger.LogWarning(ex, "Could not update bot command registration in Telegram")
            }
            :> Task

        member _.StopAsync _ = Task.CompletedTask
