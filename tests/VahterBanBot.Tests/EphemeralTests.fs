module VahterBanBot.Tests.EphemeralTests

open System
open System.Net
open System.Threading.Tasks
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

/// Ephemeral commands & confirmations (Bot API 10.2).
/// EPHEMERAL_CONFIRMATION_ENABLED defaults to false; tests that need it flip the
/// bot_setting and DisposeAsync restores it after every test (xunit creates a new
/// class instance per test).
type EphemeralTests(fixture: MlDisabledVahterTestContainers) =

    let setConfirmation (enabled: bool) = task {
        do! fixture.SetBotSetting("EPHEMERAL_CONFIRMATION_ENABLED", if enabled then "true" else "false")
        do! fixture.ReloadSettings()
    }

    [<Fact>]
    let ``Ban confirmation is sent ephemerally to the issuing vahter when enabled`` () = task {
        do! setConfirmation true

        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        let! _ = fixture.SendMessage msgUpdate

        do! fixture.ClearFakeCalls()
        let! banResp =
            Tg.replyMsg(msgUpdate.Message.Value, "/ban", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)

        let! msgBanned = fixture.MessageBanned msgUpdate.Message.Value
        Assert.True msgBanned

        // confirmation goes into the same chat, targeted at the issuing vahter only
        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.True(calls |> Array.exists (fun c ->
            c.Body.Contains $"\"chat_id\":{fixture.ChatsToMonitor[0].Id}"
            && c.Body.Contains $"\"receiver_user_id\":{fixture.Vahters[0].Id}"
            && c.Body.Contains "Banned"))
    }

    [<Fact>]
    let ``No ephemeral confirmation when the flag is off`` () = task {
        do! setConfirmation false

        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        let! _ = fixture.SendMessage msgUpdate

        do! fixture.ClearFakeCalls()
        let! banResp =
            Tg.replyMsg(msgUpdate.Message.Value, "/ban", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)

        let! msgBanned = fixture.MessageBanned msgUpdate.Message.Value
        Assert.True msgBanned

        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.False(calls |> Array.exists (fun c -> c.Body.Contains "receiver_user_id"))
    }

    [<Fact>]
    let ``Unban confirmation is sent ephemerally when enabled`` () = task {
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        let! _ = fixture.SendMessage msgUpdate
        let! _ =
            Tg.replyMsg(msgUpdate.Message.Value, "/ban", fixture.Vahters[0])
            |> fixture.SendMessage

        do! setConfirmation true
        do! fixture.ClearFakeCalls()
        let! unbanResp =
            Tg.quickMsg($"/unban {msgUpdate.Message.Value.From.Value.Id}", chat = fixture.ChatsToMonitor[0], from = fixture.Vahters[1])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, unbanResp.StatusCode)

        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.True(calls |> Array.exists (fun c ->
            c.Body.Contains $"\"receiver_user_id\":{fixture.Vahters[1].Id}"
            && c.Body.Contains "Unbanned"))
    }

    [<Fact>]
    let ``Softban confirmation carries the duration`` () = task {
        do! setConfirmation true

        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        let! _ = fixture.SendMessage msgUpdate

        do! fixture.ClearFakeCalls()
        let! sbanResp =
            Tg.replyMsg(msgUpdate.Message.Value, "/sban 12", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, sbanResp.StatusCode)

        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.True(calls |> Array.exists (fun c ->
            c.Body.Contains $"\"receiver_user_id\":{fixture.Vahters[0].Id}"
            && c.Body.Contains "Soft-banned"
            && c.Body.Contains "for 12h"))
    }

    [<Fact>]
    let ``Ephemeral command message is not deleted but the ban still happens`` () = task {
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        let! _ = fixture.SendMessage msgUpdate

        do! fixture.ClearFakeCalls()
        // command arrives as an ephemeral message — invisible to the chat, auto-expiring
        let banUpdate = Tg.replyMsg(msgUpdate.Message.Value, "/ban", fixture.Vahters[0], ephemeralMessageId = 777L)
        let! banResp = fixture.SendMessage banUpdate
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)

        let! msgBanned = fixture.MessageBanned msgUpdate.Message.Value
        Assert.True msgBanned

        let! deleteCalls = fixture.GetFakeCalls "deleteMessage"
        // the spam message is deleted...
        Assert.True(deleteCalls |> Array.exists (fun c ->
            c.Body.Contains $"\"message_id\":{msgUpdate.Message.Value.MessageId}"))
        // ...but no DeleteMessage is issued for the ephemeral command message
        Assert.False(deleteCalls |> Array.exists (fun c ->
            c.Body.Contains $"\"message_id\":{banUpdate.Message.Value.MessageId}"))
    }

    [<Fact>]
    let ``Ephemeral ping gets an ephemeral pong`` () = task {
        do! fixture.ClearFakeCalls()
        let! pingResp =
            Tg.quickMsg("/ban ping", chat = fixture.ChatsToMonitor[0], from = fixture.Vahters[0], ephemeralMessageId = 778L)
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, pingResp.StatusCode)

        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.True(calls |> Array.exists (fun c ->
            c.Body.Contains "pong"
            && c.Body.Contains $"\"receiver_user_id\":{fixture.Vahters[0].Id}"))
    }

    // Restore the confirmation flag to its default after every test.
    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetBotSetting("EPHEMERAL_CONFIRMATION_ENABLED", "false")
                do! fixture.ReloadSettings()
            } :> Task)
