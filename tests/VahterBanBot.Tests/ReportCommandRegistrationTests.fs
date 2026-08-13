module VahterBanBot.Tests.ReportCommandRegistrationTests

open VahterBanBot.Tests.ContainerTestBase
open Xunit

/// BotCommandsSetupService's one-time startup registration (see that file's doc comment):
/// with REPORT_COMMAND_ENABLED on, Telegram's group-scope command menu must advertise ONLY
/// /vahter_report — /ban, /sban, /unban must never be registered, in any scope, regardless of
/// this flag (is_ephemeral hides only the sender's command bubble, not the menu entry itself,
/// so advertising admin-only commands to the whole chat is never desirable).
type ReportCommandRegistrationTests(fixture: ReportCommandRegisteredTestContainers) =

    [<Fact>]
    let ``setMyCommands registers only vahter_report; ban/sban/unban are never registered`` () = task {
        // Deliberately does NOT call fixture.ClearFakeCalls() first — BotCommandsSetupService
        // runs once at container boot (before this test, or any other test on this dedicated
        // fixture, calls the fake API), so the startup call is still the only one recorded.
        let! setCalls = fixture.GetFakeCalls "setMyCommands"
        Assert.True(setCalls.Length > 0, "expected BotCommandsSetupService to call setMyCommands at startup with the flag on")

        let call = Array.last setCalls
        Assert.Contains("\"command\":\"vahter_report\"", call.Body)
        Assert.DoesNotContain("\"command\":\"ban\"", call.Body)
        Assert.DoesNotContain("\"command\":\"sban\"", call.Body)
        Assert.DoesNotContain("\"command\":\"unban\"", call.Body)

        // and no deleteMyCommands call — the flag is on, so nothing gets torn down at startup
        let! deleteCalls = fixture.GetFakeCalls "deleteMyCommands"
        Assert.Empty deleteCalls
    }
