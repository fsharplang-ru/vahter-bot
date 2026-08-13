module VahterBanBot.Tests.ReportCommandTests

open System
open System.Net
open System.Text.Json
open System.Threading.Tasks
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

/// Public /vahter_report (any chat member, no vahter check). REPORT_COMMAND_ENABLED defaults
/// to false; tests that need it flip the bot_setting and DisposeAsync restores it after every
/// test (xunit creates a new class instance per test) — same pattern as EphemeralTests.
type ReportCommandTests(fixture: MlDisabledVahterTestContainers) =

    let setReportEnabled (enabled: bool) = task {
        do! fixture.SetBotSetting("REPORT_COMMAND_ENABLED", if enabled then "true" else "false")
        do! fixture.ReloadSettings()
    }

    /// Extracts the JSON "text" field of a sendMessage call. The report body is Russian
    /// (Cyrillic); per AGENTS.md, raw JSON bodies must be parsed before comparing Cyrillic
    /// substrings rather than `Contains`-matched directly (System.Text.Json's default encoder
    /// \u-escapes non-ASCII, so a literal Cyrillic substring never appears verbatim in the body).
    let sentText (call: FakeCall) =
        use doc = JsonDocument.Parse call.Body
        doc.RootElement.GetProperty("text").GetString()

    [<Fact>]
    let ``Ordinary (non-vahter) member gets an ephemeral report with a distinctive substring`` () = task {
        do! setReportEnabled true

        // seed at least one message so the report has something to count
        let! _ = fixture.SendMessage(Tg.quickMsg(chat = fixture.ChatsToMonitor[0]))

        do! fixture.ClearFakeCalls()
        let reporter = Tg.user(username = "curious_member")
        let reportUpdate = Tg.quickMsg("/vahter_report", chat = fixture.ChatsToMonitor[0], from = reporter)
        let! resp = fixture.SendMessage reportUpdate
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let! calls = fixture.GetFakeCalls "sendMessage"
        let reportCall =
            calls
            |> Array.tryFind (fun c ->
                c.Body.Contains $"\"chat_id\":{fixture.ChatsToMonitor[0].Id}"
                && c.Body.Contains $"\"receiver_user_id\":{reporter.Id}")
        Assert.True(reportCall.IsSome, "expected an ephemeral sendMessage targeted at the reporter in this chat")
        Assert.Contains("Отчёт вахтёра", sentText reportCall.Value)
    }

    [<Fact>]
    let ``The report command message is not fed into the spam/message pipeline`` () = task {
        do! setReportEnabled true

        let reportUpdate = Tg.quickMsg("/vahter_report", chat = fixture.ChatsToMonitor[0])
        let! resp = fixture.SendMessage reportUpdate
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        // TryGetDbMessage reads the MessageReceived event stream, written by JustMessage/
        // ProcessMessage — a /vahter_report command must never reach that path.
        let! dbMsg = fixture.TryGetDbMessage reportUpdate.Message.Value
        Assert.False(dbMsg.IsSome, "the /vahter_report command message must not be persisted via the message pipeline")
    }

    [<Fact>]
    let ``Flag off: /vahter_report text is treated as an ordinary message (existing behavior preserved)`` () = task {
        do! setReportEnabled false

        let reportUpdate = Tg.quickMsg("/vahter_report", chat = fixture.ChatsToMonitor[0])
        let! resp = fixture.SendMessage reportUpdate
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let! dbMsg = fixture.TryGetDbMessage reportUpdate.Message.Value
        Assert.True(dbMsg.IsSome, "with the flag off, /vahter_report must fall through to the normal message pipeline")
    }

    [<Fact>]
    let ``Ephemeral report command is not deleted; a non-ephemeral one is`` () = task {
        do! setReportEnabled true

        do! fixture.ClearFakeCalls()
        let ephemeralUpdate = Tg.quickMsg("/vahter_report", chat = fixture.ChatsToMonitor[0], ephemeralMessageId = 9001L)
        let! ephResp = fixture.SendMessage ephemeralUpdate
        Assert.Equal(HttpStatusCode.OK, ephResp.StatusCode)

        let! deleteCallsAfterEphemeral = fixture.GetFakeCalls "deleteMessage"
        Assert.False(
            deleteCallsAfterEphemeral |> Array.exists (fun c ->
                c.Body.Contains $"\"message_id\":{ephemeralUpdate.Message.Value.MessageId}"),
            "an ephemeral command message is invisible/auto-expiring — DeleteMessage must not be called for it")

        do! fixture.ClearFakeCalls()
        let plainUpdate = Tg.quickMsg("/vahter_report", chat = fixture.ChatsToMonitor[0])
        let! plainResp = fixture.SendMessage plainUpdate
        Assert.Equal(HttpStatusCode.OK, plainResp.StatusCode)

        let! deleteCalls = fixture.GetFakeCalls "deleteMessage"
        Assert.True(
            deleteCalls |> Array.exists (fun c ->
                c.Body.Contains $"\"message_id\":{plainUpdate.Message.Value.MessageId}"),
            "a regular (non-ephemeral) command message must be deleted after handling")
    }

    // Restore the flag to its default after every test.
    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetBotSetting("REPORT_COMMAND_ENABLED", "false")
                do! fixture.ReloadSettings()
            } :> Task)
