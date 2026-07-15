namespace VahterBanBot

open System
open Funogram.Telegram.Types
open BotInfra

/// Wrapper around Funogram.Telegram.Types.Message.
/// All sender resolution (channel sender → SenderChat) is baked in.
/// The raw message is never mutated; enrichment text is kept separately.
type TgMessage private (raw: Message, isEdit: bool) =
    let mutable prefixText: string = null
    let mutable suffixText: string = null
    let mutable ownPhotoOcrApplied: bool = false
    let mutable externalReplyPhotoOcrApplied: bool = false

    /// Whether this message is an edit of a previously sent message.
    member _.IsEdit = isEdit

    /// True once OCR for the message's own photos is resolved — cache hit
    /// (text or empty), Azure attempt (success or failure), or initial state
    /// where there are no photos. Tells the deferred Azure step to skip.
    member _.OwnPhotoOcrApplied
        with get () = ownPhotoOcrApplied
        and  set v  = ownPhotoOcrApplied <- v

    /// Same for the external-reply quote photos.
    member _.ExternalReplyPhotoOcrApplied
        with get () = externalReplyPhotoOcrApplied
        and  set v  = externalReplyPhotoOcrApplied <- v

    // ── Sender resolution ──────────────────────────────────────────

    /// True when the message is sent on behalf of a channel
    /// (From is the technical @Channel_Bot user and SenderChat is a Channel).
    member _.IsChannelSender =
        match raw.From, raw.SenderChat with
        | Some from, Some senderChat ->
            from.IsBot
            && from.Username = Some "Channel_Bot"
            && senderChat.Type = ChatType.Channel
        | _ -> false

    /// True when the message has a resolvable sender (either a real user or a channel sender).
    member this.HasSender =
        this.IsChannelSender || raw.From.IsSome

    /// Resolved sender ID – SenderChat.Id for channel senders, From.Id otherwise.
    member this.SenderId =
        if this.IsChannelSender then raw.SenderChat.Value.Id
        else raw.From.Value.Id

    /// Resolved sender username – SenderChat.Username for channel senders, From.Username otherwise.
    /// Kept as a nullable string (not option) to preserve the pre-migration member shape.
    member this.SenderUsername : string =
        if this.IsChannelSender then raw.SenderChat.Value.Username |> Option.toObj
        else raw.From.Value.Username |> Option.toObj

    /// Human-readable display name – channel title for channel senders,
    /// FirstName + LastName (trimmed) for regular users.
    member this.SenderDisplayName =
        if this.IsChannelSender then
            let senderChat = raw.SenderChat.Value
            match senderChat.Title with
            | Some title -> title
            | None -> senderChat.Username |> Option.toObj
        else
            let from = raw.From.Value
            let lastName = defaultArg from.LastName String.Empty
            $"{from.FirstName} {lastName}".Trim()

    // ── Message identity ───────────────────────────────────────────

    member _.MessageId = raw.MessageId
    member _.ChatId    = raw.Chat.Id
    member _.ChatUsername : string = raw.Chat.Username |> Option.toObj
    member _.Chat      = raw.Chat

    // ── Text ───────────────────────────────────────────────────────

    /// The original TextOrCaption from the raw message (never enriched).
    /// Nullable string to preserve the pre-migration member shape.
    member _.OriginalText : string =
        raw.Text |> Option.orElse raw.Caption |> Option.toObj

    /// Combined text: prefix enrichments + original + suffix enrichments.
    member this.Text =
        let orig = this.OriginalText
        let hasOrig = not (String.IsNullOrWhiteSpace orig)
        let hasPrefix = not (isNull prefixText)
        let hasSuffix = not (isNull suffixText)
        match hasPrefix, hasOrig, hasSuffix with
        | false, _,    false -> orig
        | true,  false, false -> prefixText
        | false, false, true  -> suffixText
        | true,  true,  false -> $"{prefixText}\n{orig}"
        | false, true,  true  -> $"{orig}\n{suffixText}"
        | true,  false, true  -> $"{prefixText}\n{suffixText}"
        | true,  true,  true  -> $"{prefixText}\n{orig}\n{suffixText}"

    // ── Sub-objects (safe to expose – no From leakage) ─────────────

    member _.IsAutomaticForward = raw.IsAutomaticForward = Some true
    member _.Entities : MessageEntity[] option =
        if raw.Text.IsSome then raw.Entities
        else raw.CaptionEntities
    member _.Photos : PhotoSize[] = raw.Photo |> Option.defaultValue [||]
    member _.SenderChat   = raw.SenderChat
    member _.Quote        = raw.Quote
    member _.ExternalReply = raw.ExternalReply
    /// Photos of the external-reply quote (empty when there is no external reply or no photo).
    member _.ExternalReplyPhotos : PhotoSize[] =
        raw.ExternalReply |> Option.bind _.Photo |> Option.defaultValue [||]
    member _.ReplyMarkup  = raw.ReplyMarkup

    /// Wrapped reply-to message (if present).
    member _.ReplyToMessage =
        raw.ReplyToMessage |> Option.map (fun m -> TgMessage.Create m)

    // ── Enrichment (mutable, raw message stays untouched) ──────────

    /// Prepends text before the original (e.g. forwarded/quote content).
    member _.PrependText(text: string) =
        if isNull prefixText then prefixText <- text
        else prefixText <- $"{prefixText}\n{text}"

    /// Appends text after the original (e.g. OCR, inline keyboard).
    member _.AppendText(text: string) =
        if isNull suffixText then suffixText <- text
        else suffixText <- $"{suffixText}\n{text}"

    // ── Raw message access ─────────────────────────────────────────

    /// The original un-enriched raw Message. Internal to the assembly –
    /// needed for CallbackMessage serialization (backward compat) and tracing.
    member internal _.RawMessage = raw

    /// Serialized JSON of the original un-enriched raw message (for DB raw_message column).
    /// Funogram wire format (snake_case) — the same shape Telegram.Bot persisted, pinned
    /// by tests/SerializationCompat.Tests.
    member _.RawJson =
        FunogramJson.serialize raw

    // ── Factory ────────────────────────────────────────────────────

    /// Create a TgMessage from a raw Telegram Message.
    static member Create(msg: Message, ?isEdit: bool) =
        TgMessage(msg, defaultArg isEdit false)
