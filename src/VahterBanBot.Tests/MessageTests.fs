module VahterBanBot.Tests.MessageTests

open System
open System.Net
open VahterBanBot.Types
open Telegram.Bot.Types
open Telegram.Bot.Types.Enums
open VahterBanBot.Tests.ContainerTestBase
open VahterBanBot.Tests.TgMessageUtils
open Xunit
open Xunit.Extensions.AssemblyFixture

type MessageTests(fixture: VahterTestContainers) =

    [<Fact>]
    let ``All data from the message being saved`` () = task {
        // record just a message with some additional data
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        msgUpdate.Message.Entities <- [| MessageEntity(Type = MessageEntityType.Code, Offset = 0, Length = 6) |]
        msgUpdate.Message.Sticker <- Sticker(Type = StickerType.Mask, Width = 512, Height = 512, FileId = "sticker-id", FileUniqueId = "sticker-uid")
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got recorded correctly
        let! dbMsg = fixture.TryGetDbMessage msgUpdate.Message
        Assert.True dbMsg.IsSome
        
        let msg = msgUpdate.Message
        let date = DateTimeOffset(msg.Date).ToUnixTimeSeconds()

        Assert.Equal(
            dbMsg.Value,
            { chat_id = msgUpdate.Message.Chat.Id
              message_id = msgUpdate.Message.MessageId
              user_id = msgUpdate.Message.From.Id
              text = msgUpdate.Message.Text
              raw_message = $"""{{"chat": {{"id": -666, "type": "unknown", "username": "pro.hell"}}, "date": {date}, "from": {{"id": {msg.From.Id}, "is_bot": false, "first_name": "{msg.From.FirstName}"}}, "text": "{msg.Text}", "sticker": {{"type": "mask", "width": 512, "height": 512, "file_id": "sticker-id", "is_video": false, "is_animated": false, "file_unique_id": "sticker-uid"}}, "entities": [{{"type": "code", "length": 6, "offset": 0}}], "message_id": {msg.MessageId}}}"""
              created_at = dbMsg.Value.created_at }
        )                 
    }

    interface IAssemblyFixture<VahterTestContainers>
