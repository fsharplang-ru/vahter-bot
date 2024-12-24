module VahterBanBot.Tests.TgMessageUtils

open System
open System.Threading
open Telegram.Bot.Types
open Telegram.Bot.Types.Enums

type Tg() =
    static let mutable i = 1L // higher than the data in the test_seed.sql
    static let nextInt64() = Interlocked.Increment &i
    static let next() = nextInt64() |> int
    static member user (?id: int64, ?username: string, ?firstName: string) =
        User(
            Id = (id |> Option.defaultValue (nextInt64())),
            Username = (username |> Option.defaultValue null),
            FirstName = (firstName |> Option.defaultWith (fun () -> Guid.NewGuid().ToString()))
        )
    static member chat (?id: int64, ?username: string) =
        Chat(
            Id = (id |> Option.defaultValue (nextInt64())),
            Username = (username |> Option.defaultValue null),
            Type = ChatType.Supergroup
        )
    
    static member callback(data: string, ?from: User) =
        Update(
            Id = next(),
            Message = null,
            CallbackQuery = CallbackQuery(
                Id = Guid.NewGuid().ToString(),
                Data = data,
                From = (from |> Option.defaultValue (Tg.user())),
                ChatInstance = Guid.NewGuid().ToString()
            )
        )
        
    static member emoji(?offset: int) = MessageEntity(Type = MessageEntityType.CustomEmoji, Offset = defaultArg offset 0 , Length = 1)
    static member emojies(n: int) = Array.init n (fun i -> Tg.emoji i)

    static member quickMsg (?text: string, ?chat: Chat, ?from: User, ?date: DateTime, ?callback: CallbackQuery, ?caption: string, ?editedText: string, ?entities: MessageEntity[]) =
        let updateId = next()
        let msgId = next()
        Update(
            Id = updateId,
            Message = 
                Message(
                    MessageId = msgId,
                    Text = (text |> Option.defaultValue (Guid.NewGuid().ToString())),
                    Chat = (chat |> Option.defaultValue (Tg.chat())),
                    From = (from |> Option.defaultValue (Tg.user())),
                    Date = (date |> Option.defaultValue DateTime.UtcNow),
                    Caption = (caption |> Option.defaultValue null),
                    ReplyToMessage = null,
                    Entities = (entities |> Option.defaultValue null)
                ),
            EditedMessage =
                if editedText |> Option.isSome then
                    Message(
                        MessageId = msgId,
                        Text = editedText.Value,
                        Chat = (chat |> Option.defaultValue (Tg.chat())),
                        From = (from |> Option.defaultValue (Tg.user())),
                        Date = (date |> Option.defaultValue DateTime.UtcNow),
                        Caption = (caption |> Option.defaultValue null),
                        ReplyToMessage = null
                    )
                else null
            )

    static member replyMsg (msg: Message, ?text: string, ?from: User, ?date: DateTime) =
        Update(
            Id = next(),
            Message = 
                Message(
                    MessageId = next(),
                    Text = (text |> Option.defaultValue (Guid.NewGuid().ToString())),
                    Chat = msg.Chat,
                    From = (from |> Option.defaultValue (Tg.user())),
                    Date = (date |> Option.defaultValue DateTime.UtcNow),
                    ReplyToMessage = msg
                )
            )
