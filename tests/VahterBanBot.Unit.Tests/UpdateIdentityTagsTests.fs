module VahterBanBot.Unit.Tests.UpdateIdentityTagsTests

open System
open BotInfra
open BotTestInfra
open Funogram.Telegram.Types
open VahterBanBot
open Xunit

let private tags update = Telemetry.updateIdentityTags update |> Map.ofList

[<Fact>]
let ``plain message carries user, chat and message identity`` () =
    let user = Tg.user (id = 42L, username = "omgszer")
    let chat = Tg.groupChat (id = -100123L, username = "fsharp_flood")
    let update =
        Update.Create(
            updateId = 1L,
            message = Message.Create(messageId = 7L, date = DateTime.UtcNow, chat = chat, from = user, text = "hi")
        )
    let t = tags update
    Assert.Equal(box "message", t["updateType"])
    Assert.Equal(box 42L, t["fromUserId"])
    Assert.Equal(box "omgszer", t["fromUsername"])
    Assert.Equal(box -100123L, t["chatId"])
    Assert.Equal(box "fsharp_flood", t["chatUsername"])
    Assert.Equal(box 7L, t["messageId"])

[<Fact>]
let ``edited message is tagged as edited_message`` () =
    let update =
        Update.Create(
            updateId = 1L,
            editedMessage = Message.Create(messageId = 7L, date = DateTime.UtcNow, chat = Tg.groupChat (), from = Tg.user ())
        )
    Assert.Equal(box "edited_message", (tags update)["updateType"])

[<Fact>]
let ``message without From has no from tags`` () =
    let update =
        Update.Create(
            updateId = 1L,
            message = Message.Create(messageId = 7L, date = DateTime.UtcNow, chat = Tg.groupChat (id = -1L))
        )
    let t = tags update
    Assert.False(t.ContainsKey "fromUserId")
    Assert.False(t.ContainsKey "fromUsername")
    Assert.Equal(box -1L, t["chatId"])

[<Fact>]
let ``username-less user and chat produce no username tags`` () =
    let update =
        Update.Create(
            updateId = 1L,
            message = Message.Create(messageId = 7L, date = DateTime.UtcNow, chat = Tg.privateChat (), from = Tg.user (id = 5L))
        )
    let t = tags update
    Assert.Equal(box 5L, t["fromUserId"])
    Assert.False(t.ContainsKey "fromUsername")
    Assert.False(t.ContainsKey "chatUsername")

[<Fact>]
let ``callback with message carries vahter and chat identity`` () =
    let vahter = Tg.user (id = 34L, username = "vahter1")
    let chat = Tg.groupChat (id = -200L, username = "flood")
    let update =
        Update.Create(
            updateId = 1L,
            callbackQuery =
                CallbackQuery.Create(
                    id = Guid.NewGuid().ToString(),
                    from = vahter,
                    chatInstance = Guid.NewGuid().ToString(),
                    data = Guid.NewGuid().ToString(),
                    message =
                        MaybeInaccessibleMessage.Message(
                            Message.Create(messageId = 9L, date = DateTime.UtcNow, chat = chat, from = vahter)
                        )
                )
        )
    let t = tags update
    Assert.Equal(box "callback_query", t["updateType"])
    Assert.Equal(box 34L, t["fromUserId"])
    Assert.Equal(box "vahter1", t["fromUsername"])
    Assert.Equal(box -200L, t["chatId"])
    Assert.Equal(box "flood", t["chatUsername"])
    Assert.Equal(box 9L, t["messageId"])

[<Fact>]
let ``callback without message still carries vahter identity`` () =
    let update = Tg.callback (Guid.NewGuid().ToString(), from = Tg.user (id = 69L, username = "vahter2"))
    let t = tags update
    Assert.Equal(box "callback_query", t["updateType"])
    Assert.Equal(box 69L, t["fromUserId"])
    Assert.False(t.ContainsKey "chatId")

[<Fact>]
let ``reaction carries reacting user and chat identity`` () =
    let chat = Tg.groupChat (id = -300L, username = "flood")
    let update = Tg.quickReaction (chat, 11L, Tg.user (id = 8L, username = "reactor"))
    let t = tags update
    Assert.Equal(box "message_reaction", t["updateType"])
    Assert.Equal(box 8L, t["fromUserId"])
    Assert.Equal(box "reactor", t["fromUsername"])
    Assert.Equal(box -300L, t["chatId"])
    Assert.Equal(box 11L, t["messageId"])

[<Fact>]
let ``anonymous channel reaction has no from tags`` () =
    let chat = Tg.groupChat (id = -300L)
    let update = Tg.quickChannelReaction (chat, 11L, Tg.channel ())
    let t = tags update
    Assert.Equal(box "message_reaction", t["updateType"])
    Assert.False(t.ContainsKey "fromUserId")
    Assert.Equal(box -300L, t["chatId"])

[<Fact>]
let ``chat_member update is tagged by type only`` () =
    let json =
        """
        {
          "update_id": 1,
          "chat_member": {
            "chat": {"id": -400, "type": "supergroup"},
            "from": {"id": 5, "is_bot": false, "first_name": "x"},
            "date": 1700000000,
            "old_chat_member": {"status": "left", "user": {"id": 6, "is_bot": false, "first_name": "y"}},
            "new_chat_member": {"status": "member", "user": {"id": 6, "is_bot": false, "first_name": "y"}}
          }
        }
        """
    let update = (FunogramJson.parseUpdate json).Value
    let t = tags update
    Assert.Equal(1, t.Count)
    Assert.Equal(box "chat_member", t["updateType"])

[<Fact>]
let ``unknown update is tagged as unknown`` () =
    let update = Update.Create(updateId = 1L)
    let t = tags update
    Assert.Equal(1, t.Count)
    Assert.Equal(box "unknown", t["updateType"])
