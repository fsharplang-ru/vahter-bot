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
    
    static member channel (?id: int64, ?username: string, ?title: string) =
        Chat(
            Id = (id |> Option.defaultValue (nextInt64())),
            Username = (username |> Option.defaultValue null),
            Title = (title |> Option.defaultValue "Test Channel"),
            Type = ChatType.Channel
        )
    
    /// Telegram system user (id 777000) used for automatic forwards
    static member telegramUser () =
        User(
            Id = 777000L,
            IsBot = false,
            FirstName = "Telegram"
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

    static member quickMsg (?text: string, ?chat: Chat, ?from: User, ?date: DateTime, ?callback: CallbackQuery, ?caption: string, ?editedText: string, ?entities: MessageEntity[], ?photos: PhotoSize[], ?isAutomaticForward: bool, ?senderChat: Chat) =
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
                    Entities = (entities |> Option.defaultValue null),
                    Photo = (photos |> Option.defaultValue null),
                    IsAutomaticForward = (isAutomaticForward |> Option.defaultValue false),
                    SenderChat = (senderChat |> Option.defaultValue null)
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

    static member quickReaction(chat: Chat, messageId: int, from: User, ?emoji: string) =
        let reactionEmoji = emoji |> Option.defaultValue "👍"
        Update(
            Id = next(),
            MessageReaction = MessageReactionUpdated(
                Chat = chat,
                MessageId = messageId,
                User = from,
                Date = DateTime.UtcNow,
                OldReaction = [||],
                NewReaction = [| ReactionTypeEmoji(Emoji = reactionEmoji) |]
            )
        )

    static member spamPhoto =
        PhotoSize(
            FileId = "spam",
            FileUniqueId = "spam-uid",
            FileSize = 1024,
            Width = 10,
            Height = 10
        )

    static member hamPhoto =
        PhotoSize(
            FileId = "ham",
            FileUniqueId = "ham-uid",
            FileSize = 1024,
            Width = 10,
            Height = 10
        )
        
    static member bigPhoto =
        PhotoSize(
            FileId = "big-ham",
            FileUniqueId = "big-ham-uid",
            FileSize = (25 * 1024 * 1024),
            Width = 30,
            Height = 30
        )
