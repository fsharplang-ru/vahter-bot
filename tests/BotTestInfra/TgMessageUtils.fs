namespace BotTestInfra

open System
open System.Threading
open Telegram.Bot.Types
open Telegram.Bot.Types.Enums
open Telegram.Bot.Types.ReplyMarkups

type Tg() =
    // Start well above all hardcoded IDs used in tests and config to prevent
    // auto-generated user/message/chat IDs from colliding with them:
    //   Vahter IDs: 34, 69 (ALLOWED_USERS) — collision makes isBanAuthorized reject bans silently
    //   FakeTgApi admin: 42 (/getChatAdministrators)
    //   Bot user ID: 1337 (BOT_USER_ID)
    //   Seed user IDs: 1001-1010 (test_seed.sql)
    //   Seed message IDs: 10001-10499 (test_seed.sql)
    static let mutable i = 100_000L
    static let nextInt64() = Interlocked.Increment &i
    static let next() = nextInt64() |> int

    // ── User / Chat factories ────────────────────────────────────────────────

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

    static member privateChat(?id: int64) =
        Chat(
            Id = (id |> Option.defaultValue (nextInt64())),
            Type = ChatType.Private
        )

    static member groupChat(?id: int64, ?username: string) =
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

    // ── Special Telegram users ───────────────────────────────────────────────

    /// Telegram system user (id 777000) used for automatic forwards
    static member telegramUser () =
        User(
            Id = 777000L,
            IsBot = false,
            FirstName = "Telegram"
        )

    /// Telegram Channel_Bot (id 136817688) used when channels post as themselves
    static member channelBot () =
        User(
            Id = 136817688L,
            IsBot = true,
            Username = "Channel_Bot",
            FirstName = "Channel"
        )

    // ── Callback factories ───────────────────────────────────────────────────

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

    /// Callback with a Message attached (e.g. take:N or confirm_add:GUID from a private chat).
    static member dmCallback(data: string, fromUser: User) =
        let chat = Tg.privateChat(id = fromUser.Id)
        Update(
            Id = next(),
            CallbackQuery = CallbackQuery(
                Id = Guid.NewGuid().ToString(),
                Data = data,
                From = fromUser,
                ChatInstance = Guid.NewGuid().ToString(),
                Message = Message(
                    Id = next(),
                    Chat = chat,
                    From = fromUser,
                    Date = DateTime.UtcNow
                )
            )
        )

    // ── Entity helpers ───────────────────────────────────────────────────────

    static member emoji(?offset: int) = MessageEntity(Type = MessageEntityType.CustomEmoji, Offset = defaultArg offset 0, Length = 1)
    static member emojies(n: int) = Array.init n (fun i -> Tg.emoji i)

    static member textQuote(text: string) =
        TextQuote(Text = text, Position = 0)

    static member externalReply(?photos: PhotoSize[], ?chat: Chat) =
        ExternalReplyInfo(
            Photo = (photos |> Option.defaultValue null),
            Chat = (chat |> Option.defaultValue null)
        )

    // ── Message factories (VahterBanBot-style) ───────────────────────────────

    static member quickMsg (?text: string, ?chat: Chat, ?from: User, ?date: DateTime, ?callback: CallbackQuery, ?caption: string, ?editedText: string, ?entities: MessageEntity[], ?photos: PhotoSize[], ?isAutomaticForward: bool, ?senderChat: Chat, ?quote: TextQuote, ?externalReply: ExternalReplyInfo, ?replyMarkup: InlineKeyboardMarkup) =
        let updateId = next()
        let msgId = next()
        Update(
            Id = updateId,
            Message =
                Message(
                    Id = msgId,
                    Text = (text |> Option.defaultValue (Guid.NewGuid().ToString())),
                    Chat = (chat |> Option.defaultValue (Tg.chat())),
                    From = (from |> Option.defaultValue (Tg.user())),
                    Date = (date |> Option.defaultValue DateTime.UtcNow),
                    Caption = (caption |> Option.defaultValue null),
                    ReplyToMessage = null,
                    Entities = (entities |> Option.defaultValue null),
                    Photo = (photos |> Option.defaultValue null),
                    IsAutomaticForward = (isAutomaticForward |> Option.defaultValue false),
                    SenderChat = (senderChat |> Option.defaultValue null),
                    Quote = (quote |> Option.defaultValue null),
                    ExternalReply = (externalReply |> Option.defaultValue null),
                    ReplyMarkup = (replyMarkup |> Option.defaultValue null)
                ),
            EditedMessage =
                if editedText |> Option.isSome then
                    Message(
                        Id = msgId,
                        Text = editedText.Value,
                        Chat = (chat |> Option.defaultValue (Tg.chat())),
                        From = (from |> Option.defaultValue (Tg.user())),
                        Date = (date |> Option.defaultValue DateTime.UtcNow),
                        Caption = (caption |> Option.defaultValue null),
                        ReplyToMessage = null
                    )
                else null
        )

    /// Creates an edit-only Update referencing an existing message's chat+id.
    static member editMsg (msg: Message, ?text: string) =
        Update(
            Id = next(),
            EditedMessage =
                Message(
                    Id = msg.Id,
                    Text = (text |> Option.defaultValue msg.Text),
                    Chat = msg.Chat,
                    From = msg.From,
                    Date = DateTime.UtcNow
                )
        )

    static member replyMsg (msg: Message, ?text: string, ?from: User, ?date: DateTime) =
        Update(
            Id = next(),
            Message =
                Message(
                    Id = next(),
                    Text = (text |> Option.defaultValue (Guid.NewGuid().ToString())),
                    Chat = msg.Chat,
                    From = (from |> Option.defaultValue (Tg.user())),
                    Date = (date |> Option.defaultValue DateTime.UtcNow),
                    ReplyToMessage = msg
                )
            )

    static member quickReaction(chat: Chat, messageId: int, from: User, ?emoji: string) =
        let reactionEmoji = emoji |> Option.defaultValue "\U0001F44D"
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

    // ── Message factories (CouponHubBot-style) ──────────────────────────────

    static member dmMessage(text: string, fromUser: User) =
        let chat = Tg.privateChat(id = fromUser.Id)
        Update(
            Id = next(),
            Message =
                Message(
                    Id = next(),
                    Text = text,
                    From = fromUser,
                    Chat = chat,
                    Date = DateTime.UtcNow
                )
        )

    static member dmPhotoWithCaption(caption: string, fromUser: User, ?fileId: string) =
        let chat = Tg.privateChat(id = fromUser.Id)
        let fid = defaultArg fileId ($"photo-{nextInt64 ()}")
        Update(
            Id = next(),
            Message =
                Message(
                    Id = next(),
                    Caption = caption,
                    From = fromUser,
                    Chat = chat,
                    Date = DateTime.UtcNow,
                    Photo = [|
                        PhotoSize(
                            FileId = fid,
                            FileUniqueId = fid + "-uid",
                            FileSize = Nullable<int64>(1024L),
                            Width = 10,
                            Height = 10
                        )
                    |]
                )
        )

    /// Builds an Update with a text message in a group/supergroup chat.
    static member groupMessage(text: string, fromUser: User, chatId: int64, ?replyToMessageId: int) =
        let chat = Tg.groupChat(id = chatId)
        let replyTo =
            match replyToMessageId with
            | Some rid -> Message(Id = rid, Chat = chat)
            | None -> null
        Update(
            Id = next(),
            Message =
                Message(
                    Id = next(),
                    Text = text,
                    From = fromUser,
                    Chat = chat,
                    Date = DateTime.UtcNow,
                    ReplyToMessage = replyTo
                )
        )

    /// Builds an Update with a photo in a group/supergroup chat.
    static member groupPhotoMessage(fromUser: User, chatId: int64, ?caption: string, ?fileId: string) =
        let chat = Tg.groupChat(id = chatId)
        let fid = defaultArg fileId ($"group-photo-{nextInt64 ()}")
        Update(
            Id = next(),
            Message =
                Message(
                    Id = next(),
                    Caption = (defaultArg caption null),
                    From = fromUser,
                    Chat = chat,
                    Date = DateTime.UtcNow,
                    Photo = [|
                        PhotoSize(
                            FileId = fid,
                            FileUniqueId = fid + "-uid",
                            FileSize = Nullable<int64>(1024L),
                            Width = 10,
                            Height = 10
                        )
                    |]
                )
        )

    /// Builds an Update with a document in a group/supergroup chat.
    static member groupDocumentMessage(fromUser: User, chatId: int64, ?caption: string) =
        let chat = Tg.groupChat(id = chatId)
        let docFileId = $"group-doc-{nextInt64 ()}"
        Update(
            Id = next(),
            Message =
                Message(
                    Id = next(),
                    Caption = (defaultArg caption null),
                    From = fromUser,
                    Chat = chat,
                    Date = DateTime.UtcNow,
                    Document = Document(
                        FileId = docFileId,
                        FileUniqueId = docFileId + "-uid"
                    )
                )
        )

    // ── Keyboard helpers ─────────────────────────────────────────────────────

    static member inlineKeyboard(buttons: (string * string option) list) =
        let rows =
            buttons
            |> List.map (fun (text, url) ->
                seq {
                    match url with
                    | Some u -> InlineKeyboardButton.WithUrl(text, u)
                    | None -> InlineKeyboardButton.WithCallbackData(text, text)
                })
            |> List.toSeq
        InlineKeyboardMarkup(rows)

    // ── Photo fixtures ───────────────────────────────────────────────────────

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
