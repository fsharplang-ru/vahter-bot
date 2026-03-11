namespace VahterBanBot

open System
open System.Text.Json
open Telegram.Bot.Types
open Telegram.Bot.Types.Enums
open Telegram.Bot.Types.ReplyMarkups
open VahterBanBot.Utils

/// Wrapper around Telegram.Bot.Types.Message.
/// All sender resolution (channel sender → SenderChat) is baked in.
/// The raw message is never mutated; enrichment text is kept separately.
type TgMessage private (raw: Message) =
    let mutable prefixText: string = null
    let mutable suffixText: string = null

    // ── Sender resolution ──────────────────────────────────────────

    /// True when the message is sent on behalf of a channel
    /// (From is the technical @Channel_Bot user and SenderChat is a Channel).
    member _.IsChannelSender =
        raw.From <> null &&
        raw.From.IsBot &&
        raw.From.Username = "Channel_Bot" &&
        raw.SenderChat <> null &&
        raw.SenderChat.Type = ChatType.Channel

    /// True when the message has a resolvable sender (either a real user or a channel sender).
    member this.HasSender =
        this.IsChannelSender || raw.From <> null

    /// Resolved sender ID – SenderChat.Id for channel senders, From.Id otherwise.
    member this.SenderId =
        if this.IsChannelSender then raw.SenderChat.Id
        else raw.From.Id

    /// Resolved sender username – SenderChat.Username for channel senders, From.Username otherwise.
    member this.SenderUsername =
        if this.IsChannelSender then raw.SenderChat.Username
        else raw.From.Username

    // ── Message identity ───────────────────────────────────────────

    member _.MessageId = raw.MessageId
    member _.ChatId    = raw.Chat.Id
    member _.ChatUsername = raw.Chat.Username
    member _.Chat      = raw.Chat

    // ── Text ───────────────────────────────────────────────────────

    /// The original TextOrCaption from the raw message (never enriched).
    member _.OriginalText =
        if isNull raw.Text then raw.Caption
        else raw.Text

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

    member _.IsAutomaticForward = raw.IsAutomaticForward
    member _.Entities     = raw.Entities
    member _.Photos       = raw.Photo
    member _.SenderChat   = raw.SenderChat
    member _.Quote        = raw.Quote
    member _.ExternalReply = raw.ExternalReply
    member _.ReplyMarkup  = raw.ReplyMarkup

    /// Wrapped reply-to message (if present).
    member _.ReplyToMessage =
        if isNull raw.ReplyToMessage then None
        else Some (TgMessage.Create raw.ReplyToMessage)

    // ── Channel message detection (for ShouldDeleteChannelMessages) ─

    /// Matches the original isChannelMessage function – From is @Channel_Bot regardless of SenderChat.
    member _.IsChannelMessage =
        raw.From <> null &&
        raw.From.IsBot &&
        raw.From.FirstName = "Channel" &&
        raw.From.Username = "Channel_Bot"

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
    member _.RawJson =
        JsonSerializer.Serialize(raw, options = jsonOptions)

    // ── Factory ────────────────────────────────────────────────────

    /// Create a TgMessage from a raw Telegram Message.
    static member Create(msg: Message) =
        TgMessage(msg)
