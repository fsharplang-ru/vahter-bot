/// Pure unit coverage for /vahter_report: the `isReportCommand` predicate (public in Bot.fs
/// specifically so it's testable here without a container — see Bot.fs's doc comment on it)
/// and `ReportStats.ToReportMessage`, the pure Russian report formatter.
module VahterBanBot.Unit.Tests.ReportCommandTests

open BotTestInfra
open VahterBanBot
open VahterBanBot.Bot
open VahterBanBot.Types
open Xunit

let private msg (text: string) =
    TgMessage.Create(Tg.quickMsg(text = text, chat = Tg.chat()).Message.Value)

// ── isReportCommand ─────────────────────────────────────────────────────────

[<Fact>]
let ``bare /vahter_report matches`` () =
    Assert.True(isReportCommand (msg "/vahter_report"))

[<Fact>]
let ``bot-mention-suffixed command (mention-tolerant) matches`` () =
    Assert.True(isReportCommand (msg "/vahter_report@my_bot"))

[<Fact>]
let ``/vahter_report with trailing arguments still matches (only the first token is checked)`` () =
    Assert.True(isReportCommand (msg "/vahter_report extra args"))

[<Fact>]
let ``/vahter_reportx (longer command name) does NOT match`` () =
    Assert.False(isReportCommand (msg "/vahter_reportx"))

[<Fact>]
let ``/vahter (the base vahter-channel command) does NOT match`` () =
    Assert.False(isReportCommand (msg "/vahter"))

[<Fact>]
let ``mid-text mention (not the first token) does NOT match`` () =
    Assert.False(isReportCommand (msg "hey check out /vahter_report"))

[<Fact>]
let ``null text does NOT match`` () =
    Assert.False(isReportCommand (msg null))

[<Fact>]
let ``leading whitespace before the command is tolerated`` () =
    Assert.True(isReportCommand (msg "  /vahter_report"))

// ── ReportStats.ToReportMessage ─────────────────────────────────────────────

let private sampleStats =
    { Global24hTotalSeen = 500
      Global24hAutoDeletedSpam = 40
      Global24hFalsePositives = 2
      Global24hFalseNegatives = 3
      Global7dTotalSeen = 3500
      Global7dAutoDeletedSpam = 280
      Global7dFalsePositives = 9
      Global7dFalseNegatives = 15
      Chat24hTotalSeen = 60
      Chat24hAutoDeletedSpam = 5
      Chat24hFalsePositives = 0
      Chat24hFalseNegatives = 1
      Chat7dTotalSeen = 420
      Chat7dAutoDeletedSpam = 33
      Chat7dFalsePositives = 1
      Chat7dFalseNegatives = 2 }

[<Fact>]
let ``ToReportMessage renders the exact binding template with the DTO's numbers substituted`` () =
    let expected =
        String.concat "\n" [
            "🛡 Отчёт вахтёра"
            ""
            "В этом чате:"
            "• За сутки: проверено 60 сообщений, удалено 5 спам-сообщений"
            "• За неделю: проверено 420, удалено 33"
            "• Ошибочные удаления за неделю: 1 (сообщения вернули после проверки)"
            "• Спам, который люди заметили раньше бота: 2 за неделю"
            ""
            "Во всех чатах под защитой:"
            "• За сутки: проверено 500 сообщений, удалено 40 спам-сообщений"
            "• За неделю: проверено 3500, удалено 280"
            "• Ошибочные удаления: 9, спам мимо бота: 15"
            ""
            "Этот отчёт видите только вы 👀"
        ]
    Assert.Equal(expected, sampleStats.ToReportMessage())

[<Fact>]
let ``ToReportMessage stays well under Telegram's 4096-char plain-text limit`` () =
    Assert.True(sampleStats.ToReportMessage().Length < 4096)

[<Fact>]
let ``ToReportMessage is chat-scoped: chat and global sections don't cross-report each other's numbers`` () =
    let text = sampleStats.ToReportMessage()
    // chat block uses Chat7dTotalSeen (420), global block uses Global7dTotalSeen (3500) —
    // guards against accidentally wiring the wrong DTO field into either section.
    Assert.Contains("проверено 420, удалено 33", text)
    Assert.Contains("проверено 3500, удалено 280", text)
