module VahterBanBot.Tests.MessageTests

open System
open Funogram.Telegram.Types
open VahterBanBot.Types
open VahterBanBot.Utils
open VahterBanBot.Tests.ContainerTestBase
open VahterBanBot.Tests.TgMessageUtils
open Xunit
open Xunit.Extensions.AssemblyFixture

type MessageTests(fixture: VahterTestContainers) =

    [<Fact>]
    let ``All data from the message being saved`` () = task {
        // record just a message with some additional data
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        let message = msgUpdate.Message.Value
        let msgUpdate = { msgUpdate with Message = Some { message with
                                                            Entities = Some [| MessageEntity.Create(``type`` = "code", offset = 0, length = 6) |]
                                                            Sticker = Some <| Sticker.Create(``type`` = "mask", width = 512, height = 512, fileId = "sticker-id", fileUniqueId = "sticker-uid",
                                                                                     isAnimated = false,
                                                                                     isVideo = false) }
        }
        let! _ = fixture.SendMessage msgUpdate

        // assert that the message got recorded correctly
        let! dbMsg = fixture.TryGetDbMessage message
        Assert.True dbMsg.IsSome
        
        let date = DateTimeOffset(message.Date).ToUnixTimeSeconds()

        Assert.Equal(
            { chat_id = message.Chat.Id
              message_id = message.MessageId32
              user_id = message.FromId
              text = message.TextOrEmpty
              raw_message = $"""{{"chat": {{"id": -666, "type": "supergroup", "username": "pro.hell"}}, "date": {date}, "from": {{"id": {message.FromId}, "is_bot": false, "first_name": "{message.FromFirstName}"}}, "text": "{message.TextOrEmpty}", "sticker": {{"type": "mask", "width": 512, "height": 512, "file_id": "sticker-id", "is_video": false, "is_animated": false, "file_unique_id": "sticker-uid"}}, "entities": [{{"type": "code", "length": 6, "offset": 0}}], "message_id": {message.MessageId}}}"""
              created_at = dbMsg.Value.created_at },
            dbMsg.Value
        )                 
    }

    interface IAssemblyFixture<VahterTestContainers>
