namespace BotTestInfra

open System
open System.Threading
open Funogram.Telegram.Types

type Tg() =
    // Start well above all hardcoded IDs used in tests and config to prevent
    // auto-generated user/message/chat IDs from colliding with them:
    //   Vahter IDs: 34, 69 (ALLOWED_USERS) — collision makes isBanAuthorized reject bans silently
    //   FakeTgApi admin: 42 (/getChatAdministrators)
    //   Bot user ID: 1337 (BOT_USER_ID)
    //   Seed user IDs: 1001-1010 (test_seed.sql)
    //   Seed message IDs: 10001-10499 (test_seed.sql)
    static let mutable i = 100_000L
    static let next() = Interlocked.Increment &i

    /// Old builders treated an explicit `text = null` as "no text" while an omitted
    /// arg meant "random text". Preserve both behaviors on F# options.
    static let textOrRandom (text: string option) =
        match text with
        | None -> Some(Guid.NewGuid().ToString())
        | Some null -> None
        | Some t -> Some t

    // ── User / Chat factories ────────────────────────────────────────────────

    static member user (?id: int64, ?username: string, ?firstName: string) =
        User.Create(
            id = (id |> Option.defaultValue (next())),
            isBot = false,
            firstName = (firstName |> Option.defaultWith (fun () -> Guid.NewGuid().ToString())),
            ?username = username
        )

    static member chat (?id: int64, ?username: string) =
        Chat.Create(
            id = (id |> Option.defaultValue (next())),
            ``type`` = ChatType.SuperGroup,
            ?username = username
        )

    static member privateChat(?id: int64) =
        Chat.Create(
            id = (id |> Option.defaultValue (next())),
            ``type`` = ChatType.Private
        )

    static member groupChat(?id: int64, ?username: string) =
        Chat.Create(
            id = (id |> Option.defaultValue (next())),
            ``type`` = ChatType.SuperGroup,
            ?username = username
        )

    static member channel (?id: int64, ?username: string, ?title: string) =
        Chat.Create(
            id = (id |> Option.defaultValue (next())),
            ``type`` = ChatType.Channel,
            title = (title |> Option.defaultValue "Test Channel"),
            ?username = username
        )

    // ── Special Telegram users ───────────────────────────────────────────────

    /// Telegram system user (id 777000) used for automatic forwards
    static member telegramUser () =
        User.Create(id = 777000L, isBot = false, firstName = "Telegram")

    /// Telegram Channel_Bot (id 136817688) used when channels post as themselves
    static member channelBot () =
        User.Create(id = 136817688L, isBot = true, firstName = "Channel", username = "Channel_Bot")

    // ── Callback factories ───────────────────────────────────────────────────

    static member callback(data: string, ?from: User) =
        Update.Create(
            updateId = next(),
            callbackQuery = CallbackQuery.Create(
                id = Guid.NewGuid().ToString(),
                from = (from |> Option.defaultWith (fun () -> Tg.user())),
                chatInstance = Guid.NewGuid().ToString(),
                data = data
            )
        )

    /// Callback with a Message attached (e.g. take:N or confirm_add:GUID from a private chat).
    static member dmCallback(data: string, fromUser: User) =
        let chat = Tg.privateChat(id = fromUser.Id)
        Update.Create(
            updateId = next(),
            callbackQuery = CallbackQuery.Create(
                id = Guid.NewGuid().ToString(),
                from = fromUser,
                chatInstance = Guid.NewGuid().ToString(),
                data = data,
                message = MaybeInaccessibleMessage.Message(
                    Message.Create(
                        messageId = next(),
                        date = DateTime.UtcNow,
                        chat = chat,
                        from = fromUser
                    )
                )
            )
        )

    // ── Entity helpers ───────────────────────────────────────────────────────

    static member emoji(?offset: int) =
        MessageEntity.Create(``type`` = "custom_emoji", offset = int64 (defaultArg offset 0), length = 1L)
    static member emojies(n: int) = Array.init n (fun i -> Tg.emoji i)

    static member textQuote(text: string) =
        TextQuote.Create(text = text, position = 0L)

    static member externalReply(?photos: PhotoSize[], ?chat: Chat) =
        // Funogram requires a MessageOrigin; a hidden-user origin is the most neutral.
        ExternalReplyInfo.Create(
            origin = MessageOrigin.HiddenUser(
                MessageOriginHiddenUser.Create("hidden_user", DateTime.UtcNow, "hidden")),
            ?photo = photos,
            ?chat = chat
        )

    // ── Message factories (VahterBanBot-style) ───────────────────────────────

    static member quickMsg (?text: string, ?chat: Chat, ?from: User, ?date: DateTime, ?caption: string, ?editedText: string, ?entities: MessageEntity[], ?photos: PhotoSize[], ?isAutomaticForward: bool, ?senderChat: Chat, ?quote: TextQuote, ?externalReply: ExternalReplyInfo, ?replyMarkup: InlineKeyboardMarkup, ?sticker: Sticker, ?ephemeralMessageId: int64) =
        let msgId = next()
        let msgChat = chat |> Option.defaultWith (fun () -> Tg.chat())
        let msgFrom = from |> Option.defaultWith (fun () -> Tg.user())
        let msgDate = date |> Option.defaultValue DateTime.UtcNow
        Update.Create(
            updateId = next(),
            message =
                Message.Create(
                    messageId = msgId,
                    date = msgDate,
                    chat = msgChat,
                    from = msgFrom,
                    ?text = textOrRandom text,
                    ?caption = caption,
                    ?entities = entities,
                    ?photo = photos,
                    ?isAutomaticForward = isAutomaticForward,
                    ?senderChat = senderChat,
                    ?quote = quote,
                    ?externalReply = externalReply,
                    ?replyMarkup = replyMarkup,
                    ?sticker = sticker,
                    ?ephemeralMessageId = ephemeralMessageId
                ),
            ?editedMessage =
                (editedText
                 |> Option.map (fun edited ->
                     Message.Create(
                         messageId = msgId,
                         date = msgDate,
                         chat = msgChat,
                         from = msgFrom,
                         text = edited,
                         ?caption = caption
                     )))
        )

    /// Creates an edit-only Update referencing an existing message's chat+id.
    static member editMsg (msg: Message, ?text: string) =
        Update.Create(
            updateId = next(),
            editedMessage =
                Message.Create(
                    messageId = msg.MessageId,
                    date = DateTime.UtcNow,
                    chat = msg.Chat,
                    ?from = msg.From,
                    ?text = (text |> Option.map Some |> Option.defaultValue msg.Text)
                )
        )

    static member replyMsg (msg: Message, ?text: string, ?from: User, ?date: DateTime, ?ephemeralMessageId: int64) =
        Update.Create(
            updateId = next(),
            message =
                Message.Create(
                    messageId = next(),
                    date = (date |> Option.defaultValue DateTime.UtcNow),
                    chat = msg.Chat,
                    from = (from |> Option.defaultWith (fun () -> Tg.user())),
                    text = (text |> Option.defaultValue (Guid.NewGuid().ToString())),
                    replyToMessage = msg,
                    ?ephemeralMessageId = ephemeralMessageId
                )
            )

    static member quickReaction(chat: Chat, messageId: int64, from: User, ?emoji: string) =
        let reactionEmoji = emoji |> Option.defaultValue "\U0001F44D"
        Update.Create(
            updateId = next(),
            messageReaction = MessageReactionUpdated.Create(
                chat = chat,
                messageId = messageId,
                user = from,
                date = DateTime.UtcNow,
                oldReaction = [||],
                newReaction = [| ReactionType.Emoji(ReactionTypeEmoji.Create("emoji", reactionEmoji)) |]
            )
        )

    /// A reaction authored by a channel (actor_chat) rather than a user. Telegram sends ActorChat
    /// and omits User in this case (anonymous channel admin / a channel reacting on its own
    /// behalf). Exercises the missing-User guard in OnMessageReaction.
    static member quickChannelReaction(chat: Chat, messageId: int64, actorChat: Chat, ?emoji: string) =
        let reactionEmoji = emoji |> Option.defaultValue "\U0001F44D"
        Update.Create(
            updateId = next(),
            messageReaction = MessageReactionUpdated.Create(
                chat = chat,
                messageId = messageId,
                actorChat = actorChat,
                date = DateTime.UtcNow,
                oldReaction = [||],
                newReaction = [| ReactionType.Emoji(ReactionTypeEmoji.Create("emoji", reactionEmoji)) |]
            )
        )

    // ── Message factories (CouponHubBot-style) ──────────────────────────────

    static member dmMessage(text: string, fromUser: User) =
        let chat = Tg.privateChat(id = fromUser.Id)
        Update.Create(
            updateId = next(),
            message =
                Message.Create(
                    messageId = next(),
                    date = DateTime.UtcNow,
                    chat = chat,
                    from = fromUser,
                    text = text
                )
        )

    static member dmPhotoWithCaption(caption: string, fromUser: User, ?fileId: string) =
        let chat = Tg.privateChat(id = fromUser.Id)
        let fid = defaultArg fileId ($"photo-{next ()}")
        Update.Create(
            updateId = next(),
            message =
                Message.Create(
                    messageId = next(),
                    date = DateTime.UtcNow,
                    chat = chat,
                    from = fromUser,
                    caption = caption,
                    photo = [| PhotoSize.Create(fid, fid + "-uid", 10L, 10L, fileSize = 1024L) |]
                )
        )

    /// Album photo (sets MediaGroupId so the bot's album router fires). Each photo
    /// of a real Telegram album shares the same media_group_id but has a distinct
    /// message_id and file_id. Tests pass an explicit messageId to assert
    /// reply_parameters.message_id matches in per-failed-photo replies.
    static member dmAlbumPhoto(fromUser: User, mediaGroupId: string, ?fileId: string, ?messageId: int64, ?caption: string) =
        let chat = Tg.privateChat(id = fromUser.Id)
        let fid = defaultArg fileId ($"album-photo-{next ()}")
        let mid = defaultArg messageId (next())
        Update.Create(
            updateId = next(),
            message =
                Message.Create(
                    messageId = mid,
                    date = DateTime.UtcNow,
                    chat = chat,
                    from = fromUser,
                    ?caption = caption,
                    mediaGroupId = mediaGroupId,
                    photo = [| PhotoSize.Create(fid, fid + "-uid", 10L, 10L, fileSize = 1024L) |]
                )
        )

    /// Builds an Update with a text message in a group/supergroup chat.
    static member groupMessage(text: string, fromUser: User, chatId: int64, ?replyToMessageId: int64) =
        let chat = Tg.groupChat(id = chatId)
        Update.Create(
            updateId = next(),
            message =
                Message.Create(
                    messageId = next(),
                    date = DateTime.UtcNow,
                    chat = chat,
                    from = fromUser,
                    text = text,
                    ?replyToMessage =
                        (replyToMessageId
                         |> Option.map (fun rid -> Message.Create(messageId = rid, date = DateTime.UtcNow, chat = chat)))
                )
        )

    /// Builds an Update with a photo in a group/supergroup chat.
    static member groupPhotoMessage(fromUser: User, chatId: int64, ?caption: string, ?fileId: string) =
        let chat = Tg.groupChat(id = chatId)
        let fid = defaultArg fileId ($"group-photo-{next ()}")
        Update.Create(
            updateId = next(),
            message =
                Message.Create(
                    messageId = next(),
                    date = DateTime.UtcNow,
                    chat = chat,
                    from = fromUser,
                    ?caption = caption,
                    photo = [| PhotoSize.Create(fid, fid + "-uid", 10L, 10L, fileSize = 1024L) |]
                )
        )

    /// Builds an Update with a document in a group/supergroup chat.
    static member groupDocumentMessage(fromUser: User, chatId: int64, ?caption: string) =
        let chat = Tg.groupChat(id = chatId)
        let docFileId = $"group-doc-{next ()}"
        Update.Create(
            updateId = next(),
            message =
                Message.Create(
                    messageId = next(),
                    date = DateTime.UtcNow,
                    chat = chat,
                    from = fromUser,
                    ?caption = caption,
                    document = Document.Create(docFileId, docFileId + "-uid")
                )
        )

    // ── Keyboard helpers ─────────────────────────────────────────────────────

    static member inlineKeyboard(buttons: (string * string option) list) =
        let rows =
            [| for (text, url) in buttons ->
                 [| match url with
                    | Some u -> InlineKeyboardButton.Create(text, url = u)
                    | None -> InlineKeyboardButton.Create(text, callbackData = text) |] |]
        InlineKeyboardMarkup.Create(rows)

    // ── Photo fixtures ───────────────────────────────────────────────────────

    static member spamPhoto =
        PhotoSize.Create("spam", "spam-uid", 10L, 10L, fileSize = 1024L)

    static member hamPhoto =
        PhotoSize.Create("ham", "ham-uid", 10L, 10L, fileSize = 1024L)

    static member bigPhoto =
        PhotoSize.Create("big-ham", "big-ham-uid", 30L, 30L, fileSize = 25L * 1024L * 1024L)
