module VahterBanBot.Tests.MessageTests

open System
open System.Net
open System.Text
open System.Text.Json
open VahterBanBot.Types
open Funogram.Telegram.Types
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

type MessageTests(fixture: MlDisabledVahterTestContainers) =

    [<Fact>]
    let ``All data from the message being saved`` () = task {
        // record just a message with some additional data
        let msgUpdate =
            Tg.quickMsg(
                chat = fixture.ChatsToMonitor[0],
                entities = [| MessageEntity.Create("code", 0L, 6L) |],
                sticker = Sticker.Create("sticker-id", "sticker-uid", "mask", 512L, 512L, false, false)
            )
        let! _ = fixture.SendMessage msgUpdate
        let msg = msgUpdate.Message.Value

        // assert that the message got recorded correctly
        let! dbMsg = fixture.TryGetDbMessage msg
        Assert.True dbMsg.IsSome
        let dbMsg = dbMsg.Value

        Assert.Equal(msg.Chat.Id, dbMsg.chat_id)
        Assert.Equal(msg.MessageId, dbMsg.message_id)
        Assert.Equal(msg.From.Value.Id, dbMsg.user_id)
        Assert.Equal(msg.Text.Value, dbMsg.text)

        // raw_message is the Funogram wire JSON of the update this test sent.
        // Property order and omitted-when-None fields are serializer details, so
        // compare the fields the bot's SQL/analytics actually read — semantically.
        use rawDoc = JsonDocument.Parse(dbMsg.raw_message)
        let raw = rawDoc.RootElement
        Assert.Equal(msg.MessageId, raw.GetProperty("message_id").GetInt64())
        Assert.Equal(DateTimeOffset(msg.Date).ToUnixTimeSeconds(), raw.GetProperty("date").GetInt64())
        Assert.Equal(msg.Chat.Id, raw.GetProperty("chat").GetProperty("id").GetInt64())
        Assert.Equal("supergroup", raw.GetProperty("chat").GetProperty("type").GetString())
        Assert.Equal(msg.From.Value.Id, raw.GetProperty("from").GetProperty("id").GetInt64())
        Assert.Equal(msg.From.Value.FirstName, raw.GetProperty("from").GetProperty("first_name").GetString())
        Assert.False(raw.GetProperty("from").GetProperty("is_bot").GetBoolean())
        Assert.Equal(msg.Text.Value, raw.GetProperty("text").GetString())
        Assert.Equal("sticker-id", raw.GetProperty("sticker").GetProperty("file_id").GetString())
        Assert.Equal("mask", raw.GetProperty("sticker").GetProperty("type").GetString())
        let entity = raw.GetProperty("entities")[0]
        Assert.Equal("code", entity.GetProperty("type").GetString())
        Assert.Equal(0L, entity.GetProperty("offset").GetInt64())
        Assert.Equal(6L, entity.GetProperty("length").GetInt64())
    }

    [<Fact>]
    let ``MlData counts custom_emoji entities from live rows and tolerates backfill rows`` () = task {
        // Live path (issue #166): the webhook stores rawMessage as a JSON *string*;
        // MlData must unwrap it before reading entities, or custom_emoji_count is
        // silently 0 for every live row.
        let msgUpdate =
            Tg.quickMsg(
                chat = fixture.ChatsToMonitor[0],
                text = "emoji spam candidate",
                entities =
                    [| MessageEntity.Create("custom_emoji", 0L, 2L, customEmojiId = "111")
                       MessageEntity.Create("custom_emoji", 2L, 2L, customEmojiId = "222")
                       MessageEntity.Create("code", 4L, 5L) |]
            )
        let! _ = fixture.SendMessage msgUpdate

        // V27-backfill row in its post-V40 shape: rawMessage is the string "{}" (no entities).
        use conn = new Npgsql.NpgsqlConnection(fixture.DbConnectionString)
        //language=postgresql
        let seedSql =
            """
INSERT INTO event(stream_id, stream_version, data, created_at)
VALUES ('message:-666:999777', 1,
        jsonb_build_object('Case', 'MessageReceived', 'chatId', -666, 'messageId', 999777,
                           'userId', 42, 'text', 'backfilled spam', 'rawMessage', '{}'),
        now())
ON CONFLICT DO NOTHING
            """
        let! _ = Dapper.SqlMapper.ExecuteAsync(conn, seedSql)

        let db = VahterBanBot.DbService(fixture.DbConnectionString, TimeProvider.System)
        let! mlData = db.MlData(100, DateTime.UtcNow.AddDays -1.0)

        let byText t = mlData |> Array.find (fun x -> x.text = t)
        Assert.Equal(2, (byText "emoji spam candidate").custom_emoji_count)
        Assert.Equal(0, (byText "backfilled spam").custom_emoji_count)
    }

    [<Fact>]
    let ``Photo messages are processed without OCR when disabled`` () = task {
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "hello-from-photo", photos = [|Tg.hamPhoto|])

        let! _ = fixture.SendMessage msgUpdate

        let! dbMsg = fixture.TryGetDbMessage msgUpdate.Message.Value

        Assert.Equal("hello-from-photo", dbMsg.Value.text)
    }

    [<Fact>]
    let ``Spammy photo content is ignored when OCR disabled`` () = task {
        let spamOnly = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = null, photos = [|Tg.spamPhoto|])

        let! _ = fixture.SendMessage spamOnly

        let! dbMsg = fixture.TryGetDbMessage spamOnly.Message.Value

        Assert.Null(dbMsg.Value.text)
    }

    [<Fact>]
    let ``Editing a media message stored with null text succeeds`` () = task {
        let chat = fixture.ChatsToMonitor[0]

        // Send photo-only message (no text) — bot records MessageReceived with text = null
        let original = Tg.quickMsg(chat = chat, text = null, photos = [| Tg.hamPhoto |])
        let! _ = fixture.SendMessage original

        let! dbMsg = fixture.TryGetDbMessage original.Message.Value
        Assert.Null(dbMsg.Value.text)

        // Send edit update for the same message
        let editUpdate = Tg.editMsg(original.Message.Value, text = "caption added on edit")
        let! resp = fixture.SendMessage editUpdate
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        // Assert MessageEdited event was recorded
        let! editRecorded = fixture.MessageEditedRecorded(chat.Id, original.Message.Value.MessageId)
        Assert.True editRecorded
    }

    [<Fact>]
    let ``Message reaction update returns OK`` () = task {
        // first send a message to have a valid message_id
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        let! _ = fixture.SendMessage msgUpdate

        // send a reaction update for that message
        let reactionUpdate = Tg.quickReaction(
            chat = fixture.ChatsToMonitor[0],
            messageId = msgUpdate.Message.Value.MessageId,
            from = Tg.user()
        )
        let! resp = fixture.SendMessage reactionUpdate
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
    }

