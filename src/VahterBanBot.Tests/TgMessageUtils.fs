module VahterBanBot.Tests.TgMessageUtils

open System
open System.Threading
open Funogram.Telegram.Types

type Tg() =
    static let mutable i = 1L // higher than the data in the test_seed.sql
    static let nextInt64() = Interlocked.Increment &i
    static let next() = nextInt64() |> int
    static member user (?id: int64, ?username: string, ?firstName: string) =
        User.Create(
            id = (id |> Option.defaultValue (nextInt64())),
            isBot = false,
            username = (username |> Option.defaultValue null),
            firstName = (firstName |> Option.defaultWith (fun () -> Guid.NewGuid().ToString()))
        )
    static member chat (?id: int64, ?username: string) =
        Chat.Create(
            id = (id |> Option.defaultValue (nextInt64())),
            username = (username |> Option.defaultValue null),
            ``type`` = ChatType.SuperGroup
        )
    
    static member callback(data: string, ?from: User) =
        Update.Create(
            updateId = next(),
            callbackQuery = CallbackQuery.Create(
                id = Guid.NewGuid().ToString(),
                data = data,
                from = (from |> Option.defaultValue (Tg.user())),
                chatInstance = Guid.NewGuid().ToString()
            )
        )

    static member quickMsg (?text: string, ?chat: Chat, ?from: User, ?date: DateTime, ?callback: CallbackQuery, ?caption: string, ?editedText: string) =
        let updateId = next()
        let msgId = next()
        Update.Create(
            updateId,
            message = Message.Create(
                messageId = msgId,
                text = (text |> Option.defaultValue (Guid.NewGuid().ToString())),
                chat = (chat |> Option.defaultValue (Tg.chat())),
                from = (from |> Option.defaultValue (Tg.user())),
                date = (date |> Option.defaultValue DateTime.UtcNow),
                caption = (caption |> Option.defaultValue null)
            ),
            ?editedMessage = (
                editedText |> Option.map (fun editedText ->
                    Message.Create(
                        messageId = msgId,
                        text = editedText,
                        chat = (chat |> Option.defaultValue (Tg.chat())),
                        from = (from |> Option.defaultValue (Tg.user())),
                        date = (date |> Option.defaultValue DateTime.UtcNow),
                        caption = (caption |> Option.defaultValue null)
                    )
                )
            )
        )

    static member replyMsg (msg: Message, ?text: string, ?from: User, ?date: DateTime) =
        Update.Create(
            updateId = next(),
            message = 
                Message.Create(
                    messageId = next(),
                    text = (text |> Option.defaultValue (Guid.NewGuid().ToString())),
                    chat = msg.Chat,
                    from = (from |> Option.defaultValue (Tg.user())),
                    date = (date |> Option.defaultValue DateTime.UtcNow),
                    replyToMessage = msg
                )
            )
