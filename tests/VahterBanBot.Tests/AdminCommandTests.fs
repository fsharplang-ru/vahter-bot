module VahterBanBot.Tests.AdminCommandTests

open System
open System.Net
open System.Threading.Tasks
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

// The container/DB fixture is shared across the whole assembly, and admin commands
// mutate the live CHATS_TO_MONITOR / ALLOWED_USERS settings. To keep tests isolated
// we restore both blobs to their seed values after every test via IAsyncDisposable.
let private seedChats = """{"pro.hell":"-666","dotnetru":-42}"""
let private seedVahters = """{"vahter_1":"34","vahter_2":69}"""

type AdminCommandTests(fixture: MlDisabledVahterTestContainers) =

    // ── Authorization & footprint ────────────────────────────────────────────

    [<Fact>]
    let ``vahter help replies with command list in admin channel`` () = task {
        do! fixture.ClearFakeCalls()
        let! resp =
            Tg.quickMsg(text = "/vahter help", chat = fixture.AdminChannel, from = fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.True(calls |> Array.exists (fun c ->
            c.Body.Contains "\"chat_id\":-200" && c.Body.Contains "Vahter admin commands"))
    }

    [<Fact>]
    let ``vahter command tolerates bot mention suffix`` () = task {
        do! fixture.ClearFakeCalls()
        let! _ =
            Tg.quickMsg(text = "/vahter@test_bot help", chat = fixture.AdminChannel, from = fixture.Vahters[0])
            |> fixture.SendMessage
        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.True(calls |> Array.exists (fun c -> c.Body.Contains "Vahter admin commands"))
    }

    [<Fact>]
    let ``unknown subcommand replies with help`` () = task {
        do! fixture.ClearFakeCalls()
        let! _ =
            Tg.quickMsg(text = "/vahter frobnicate", chat = fixture.AdminChannel, from = fixture.Vahters[0])
            |> fixture.SendMessage
        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.True(calls |> Array.exists (fun c -> c.Body.Contains "Unknown command"))
    }

    [<Fact>]
    let ``non-vahter cannot use admin commands`` () = task {
        // user 42 (just_admin) is a chat admin but NOT in ALLOWED_USERS
        let! resp =
            Tg.quickMsg(text = "/vahter addchat -555111 nope", chat = fixture.AdminChannel, from = fixture.Admins[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        let! chats = fixture.GetBotSetting "CHATS_TO_MONITOR"
        Assert.False(chats.Value.Contains "-555111")
    }

    [<Fact>]
    let ``admin command in monitored chat is ignored (zero footprint)`` () = task {
        let msg = Tg.quickMsg(text = "/vahter addchat -555222 nope", chat = fixture.ChatsToMonitor[0], from = fixture.Vahters[0])
        let! resp = fixture.SendMessage msg
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        // settings unchanged
        let! chats = fixture.GetBotSetting "CHATS_TO_MONITOR"
        Assert.False(chats.Value.Contains "-555222")
        // and the text was treated as an ordinary message
        let! dbMsg = fixture.TryGetDbMessage msg.Message.Value
        Assert.True dbMsg.IsSome
    }

    // ── addchat / removechat ─────────────────────────────────────────────────

    [<Fact>]
    let ``addchat with explicit id and username adds chat`` () = task {
        let! resp =
            Tg.quickMsg(text = "/vahter addchat -555333 newchat", chat = fixture.AdminChannel, from = fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        let! chats = fixture.GetBotSetting "CHATS_TO_MONITOR"
        Assert.True(chats.Value.Contains "-555333")
        Assert.True(chats.Value.Contains "newchat")
    }

    [<Fact>]
    let ``addchat by username resolves via getChat`` () = task {
        do! fixture.SetMockChat("resolved_chat", -555444L, "Resolved Chat")
        do! fixture.ClearFakeCalls()
        let! resp =
            Tg.quickMsg(text = "/vahter addchat @resolved_chat", chat = fixture.AdminChannel, from = fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        let! getChatCalls = fixture.GetFakeCalls "getChat"
        Assert.NotEmpty getChatCalls
        let! chats = fixture.GetBotSetting "CHATS_TO_MONITOR"
        Assert.True(chats.Value.Contains "-555444")
    }

    [<Fact>]
    let ``addchat with no args leaves config unchanged`` () = task {
        let! before = fixture.GetBotSetting "CHATS_TO_MONITOR"
        let! _ =
            Tg.quickMsg(text = "/vahter addchat", chat = fixture.AdminChannel, from = fixture.Vahters[0])
            |> fixture.SendMessage
        let! after = fixture.GetBotSetting "CHATS_TO_MONITOR"
        Assert.Equal(before.Value, after.Value)
    }

    [<Fact>]
    let ``removechat by id removes a chat`` () = task {
        let! _ =
            Tg.quickMsg(text = "/vahter addchat -555555 tmpchat", chat = fixture.AdminChannel, from = fixture.Vahters[0])
            |> fixture.SendMessage
        let! _ =
            Tg.quickMsg(text = "/vahter removechat -555555", chat = fixture.AdminChannel, from = fixture.Vahters[0])
            |> fixture.SendMessage
        let! chats = fixture.GetBotSetting "CHATS_TO_MONITOR"
        Assert.False(chats.Value.Contains "-555555")
        // seeded chats remain intact
        Assert.True(chats.Value.Contains "-666")
    }

    [<Fact>]
    let ``removechat by username removes a chat`` () = task {
        let! _ =
            Tg.quickMsg(text = "/vahter addchat -555666 byebye", chat = fixture.AdminChannel, from = fixture.Vahters[0])
            |> fixture.SendMessage
        let! _ =
            Tg.quickMsg(text = "/vahter removechat @byebye", chat = fixture.AdminChannel, from = fixture.Vahters[0])
            |> fixture.SendMessage
        let! chats = fixture.GetBotSetting "CHATS_TO_MONITOR"
        Assert.False(chats.Value.Contains "byebye")
        Assert.False(chats.Value.Contains "-555666")
    }

    // ── addvahter / removevahter ─────────────────────────────────────────────

    [<Fact>]
    let ``addvahter grants admin rights to a new user`` () = task {
        let newVahter = Tg.user(id = 555777L, username = "new_vahter")
        let! _ =
            Tg.quickMsg(text = "/vahter addvahter 555777 new_vahter", chat = fixture.AdminChannel, from = fixture.Vahters[0])
            |> fixture.SendMessage
        let! vahters = fixture.GetBotSetting "ALLOWED_USERS"
        Assert.True(vahters.Value.Contains "555777")
        // behavioral: the new vahter can now run an admin command
        let! _ =
            Tg.quickMsg(text = "/vahter addchat -555888 viaNew", chat = fixture.AdminChannel, from = newVahter)
            |> fixture.SendMessage
        let! chats = fixture.GetBotSetting "CHATS_TO_MONITOR"
        Assert.True(chats.Value.Contains "-555888")
    }

    [<Fact>]
    let ``removevahter revokes admin rights`` () = task {
        let tmpVahter = Tg.user(id = 555999L, username = "tmp_vahter")
        let! _ =
            Tg.quickMsg(text = "/vahter addvahter 555999 tmp_vahter", chat = fixture.AdminChannel, from = fixture.Vahters[0])
            |> fixture.SendMessage
        let! _ =
            Tg.quickMsg(text = "/vahter removevahter 555999", chat = fixture.AdminChannel, from = fixture.Vahters[0])
            |> fixture.SendMessage
        let! vahters = fixture.GetBotSetting "ALLOWED_USERS"
        Assert.False(vahters.Value.Contains "555999")
        // behavioral: the removed vahter can no longer change config
        let! _ =
            Tg.quickMsg(text = "/vahter addchat -556000 shouldNotWork", chat = fixture.AdminChannel, from = tmpVahter)
            |> fixture.SendMessage
        let! chats = fixture.GetBotSetting "CHATS_TO_MONITOR"
        Assert.False(chats.Value.Contains "-556000")
    }

    [<Fact>]
    let ``addvahter by username is rejected`` () = task {
        do! fixture.ClearFakeCalls()
        let! _ =
            Tg.quickMsg(text = "/vahter addvahter @someone", chat = fixture.AdminChannel, from = fixture.Vahters[0])
            |> fixture.SendMessage
        let! vahters = fixture.GetBotSetting "ALLOWED_USERS"
        Assert.False(vahters.Value.Contains "someone")
        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.True(calls |> Array.exists (fun c -> c.Body.Contains "not supported"))
    }

    [<Fact>]
    let ``addvahter with non-numeric id replies usage`` () = task {
        do! fixture.ClearFakeCalls()
        let! _ =
            Tg.quickMsg(text = "/vahter addvahter notanid bob", chat = fixture.AdminChannel, from = fixture.Vahters[0])
            |> fixture.SendMessage
        let! vahters = fixture.GetBotSetting "ALLOWED_USERS"
        Assert.False(vahters.Value.Contains "bob")
        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.True(calls |> Array.exists (fun c -> c.Body.Contains "Invalid user id"))
    }

    // ── retrain / cleanup (fire-and-forget acks) ─────────────────────────────

    [<Fact>]
    let ``retrain replies with an ack`` () = task {
        do! fixture.ClearFakeCalls()
        let! resp =
            Tg.quickMsg(text = "/vahter retrain", chat = fixture.AdminChannel, from = fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.True(calls |> Array.exists (fun c ->
            c.Body.Contains "\"chat_id\":-200" && c.Body.Contains "Retrain started"))
    }

    [<Fact>]
    let ``cleanup replies with an ack`` () = task {
        do! fixture.ClearFakeCalls()
        let! resp =
            Tg.quickMsg(text = "/vahter cleanup", chat = fixture.AdminChannel, from = fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.True(calls |> Array.exists (fun c -> c.Body.Contains "Cleanup started"))
    }

    // ── unmarkspam ───────────────────────────────────────────────────────────

    [<Fact>]
    let ``spam-deletion log contains a parseable ref token`` () = task {
        // Ban a user, then have them post again -> the "already banned" deletion
        // path logs to the AllLogs channel with the #ref token. This proves the
        // producer side (token emitted) without needing the ML pipeline.
        let spammer = Tg.user()
        let firstMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], from = spammer)
        let! _ = fixture.SendMessage firstMsg
        let! _ = Tg.replyMsg(firstMsg.Message.Value, "/ban", fixture.Vahters[0]) |> fixture.SendMessage
        let! banned = fixture.MessageBanned firstMsg.Message.Value
        Assert.True banned

        do! fixture.ClearFakeCalls()
        let secondMsg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], from = spammer)
        let! _ = fixture.SendMessage secondMsg
        let! calls = fixture.GetFakeCalls "sendMessage"
        let token = $"#ref:{secondMsg.Message.Value.Chat.Id}:{secondMsg.Message.Value.MessageId}"
        Assert.True(calls |> Array.exists (fun c -> c.Body.Contains token))
    }

    [<Fact>]
    let ``unmarkspam reverses spam mark via forwarded ref token`` () = task {
        let chatId = -666L
        let messageId = 123456
        // Simulate a forwarded bot spam-deletion log post carrying the #ref token.
        let logText = $"Deleted spam (ml) in @pro.hell ({chatId}) from @bad (999) with text:\nbuy now\n#ref:{chatId}:{messageId}"
        let logMsg = Tg.quickMsg(text = logText, chat = fixture.AdminChannel, from = fixture.Vahters[0])
        let! resp =
            Tg.replyMsg(logMsg.Message.Value, "/vahter unmarkspam", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        let! ham = fixture.MessageMarkedHam(chatId, messageId)
        Assert.True ham
    }

    [<Fact>]
    let ``unmarkspam without a reply does nothing`` () = task {
        do! fixture.ClearFakeCalls()
        let! _ =
            Tg.quickMsg(text = "/vahter unmarkspam", chat = fixture.AdminChannel, from = fixture.Vahters[0])
            |> fixture.SendMessage
        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.True(calls |> Array.exists (fun c -> c.Body.Contains "Reply to a forwarded"))
    }

    [<Fact>]
    let ``unmarkspam reply without a ref token does nothing`` () = task {
        let plain = Tg.quickMsg(text = "just a normal message, no reference here", chat = fixture.AdminChannel, from = fixture.Vahters[0])
        do! fixture.ClearFakeCalls()
        let! _ =
            Tg.replyMsg(plain.Message.Value, "/vahter unmarkspam", fixture.Vahters[0])
            |> fixture.SendMessage
        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.True(calls |> Array.exists (fun c -> c.Body.Contains "Could not find"))
    }

    // ── markspam ─────────────────────────────────────────────────────────────

    [<Fact>]
    let ``markspam marks message as spam via forwarded ref token`` () = task {
        let chatId = -667L
        let messageId = 654321
        // Simulate a forwarded bot log post carrying the #ref token.
        let logText = $"Deleted spam (ml) in @pro.hell ({chatId}) from @bad (999) with text:\nbuy now\n#ref:{chatId}:{messageId}"
        let logMsg = Tg.quickMsg(text = logText, chat = fixture.AdminChannel, from = fixture.Vahters[0])
        let! resp =
            Tg.replyMsg(logMsg.Message.Value, "/vahter markspam", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
        let! spam = fixture.MessageMarkedSpam(chatId, messageId)
        Assert.True spam
    }

    [<Fact>]
    let ``markspam reverses an erroneous unmarkspam`` () = task {
        let chatId = -668L
        let messageId = 654322
        let logText = $"Deleted spam (ml) in @pro.hell ({chatId}) from @bad (999) with text:\nbuy now\n#ref:{chatId}:{messageId}"
        let logMsg = Tg.quickMsg(text = logText, chat = fixture.AdminChannel, from = fixture.Vahters[0])
        let! _ =
            Tg.replyMsg(logMsg.Message.Value, "/vahter unmarkspam", fixture.Vahters[0])
            |> fixture.SendMessage
        let! _ =
            Tg.replyMsg(logMsg.Message.Value, "/vahter markspam", fixture.Vahters[0])
            |> fixture.SendMessage
        let! ham = fixture.MessageMarkedHam(chatId, messageId)
        let! spam = fixture.MessageMarkedSpam(chatId, messageId)
        Assert.True ham
        Assert.True spam
    }

    [<Fact>]
    let ``markspam without a reply does nothing`` () = task {
        do! fixture.ClearFakeCalls()
        let! _ =
            Tg.quickMsg(text = "/vahter markspam", chat = fixture.AdminChannel, from = fixture.Vahters[0])
            |> fixture.SendMessage
        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.True(calls |> Array.exists (fun c -> c.Body.Contains "Reply to a forwarded"))
    }

    [<Fact>]
    let ``markspam reply without a ref token does nothing`` () = task {
        let plain = Tg.quickMsg(text = "just a normal message, no reference here", chat = fixture.AdminChannel, from = fixture.Vahters[0])
        do! fixture.ClearFakeCalls()
        let! _ =
            Tg.replyMsg(plain.Message.Value, "/vahter markspam", fixture.Vahters[0])
            |> fixture.SendMessage
        let! calls = fixture.GetFakeCalls "sendMessage"
        Assert.True(calls |> Array.exists (fun c -> c.Body.Contains "Could not find"))
    }

    // Restore the shared config blobs to their seed values after every test.
    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetBotSetting("CHATS_TO_MONITOR", seedChats)
                do! fixture.SetBotSetting("ALLOWED_USERS", seedVahters)
                do! fixture.ReloadSettings()
            } :> Task)
