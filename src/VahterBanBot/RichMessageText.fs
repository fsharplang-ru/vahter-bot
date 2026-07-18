/// Flattens Bot API 10.1 rich messages (Message.RichMessage) into plain text
/// for the ML classifier. Spammers hide their pitch in rich blocks (tables,
/// formulas, collapsed details) with no Text/Caption at all, so every
/// human-visible string AND every machine payload (URLs, usernames, LaTeX
/// sources, emoji alternative text) must end up in the flattened output.
module VahterBanBot.RichMessageText

open System
open System.Text
open Funogram.Telegram.Types
open BotInfra

/// Breaks the line unless the buffer is empty or already ends with one —
/// keeps the output newline-separated without stacking blank lines.
let private appendLineBreak (sb: StringBuilder) =
    if sb.Length > 0 && sb[sb.Length - 1] <> '\n' then
        %sb.Append '\n'

/// Machine payload (URL, username, LaTeX source, …) gets its own line right
/// after the visible text it belongs to — same shape the inline-keyboard
/// enricher produces (button text, then button URL).
let private appendPayload (sb: StringBuilder) (payload: string) =
    if not (String.IsNullOrWhiteSpace payload) then
        appendLineBreak sb
        %sb.Append payload
        appendLineBreak sb

// Rich-message custom emojis are not message entities, so the ML
// custom_emoji_count feature does not see them; their AlternativeText below
// partially compensates via the text featurizer.
let rec private appendRichText (sb: StringBuilder) (richText: RichText) =
    match richText with
    | RichText.Plain text -> %sb.Append text
    | RichText.ArrayOf items -> for item in items do appendRichText sb item
    | RichText.Bold t -> appendRichText sb t.Text
    | RichText.Italic t -> appendRichText sb t.Text
    | RichText.Underline t -> appendRichText sb t.Text
    | RichText.Strikethrough t -> appendRichText sb t.Text
    | RichText.Spoiler t -> appendRichText sb t.Text
    | RichText.Subscript t -> appendRichText sb t.Text
    | RichText.Superscript t -> appendRichText sb t.Text
    | RichText.Marked t -> appendRichText sb t.Text
    | RichText.Code t -> appendRichText sb t.Text
    | RichText.DateTimeText t -> appendRichText sb t.Text
    | RichText.TextMention t -> appendRichText sb t.Text
    | RichText.CustomEmoji e -> %sb.Append e.AlternativeText
    | RichText.MathematicalExpression m -> appendPayload sb m.Expression
    | RichText.Url u ->
        appendRichText sb u.Text
        appendPayload sb u.Url
    | RichText.EmailAddress e ->
        appendRichText sb e.Text
        appendPayload sb e.EmailAddress
    | RichText.PhoneNumber p ->
        appendRichText sb p.Text
        appendPayload sb p.PhoneNumber
    | RichText.BankCardNumber b ->
        appendRichText sb b.Text
        appendPayload sb b.BankCardNumber
    | RichText.Mention m ->
        appendRichText sb m.Text
        appendPayload sb m.Username
    | RichText.Hashtag h ->
        appendRichText sb h.Text
        appendPayload sb h.Hashtag
    | RichText.Cashtag c ->
        appendRichText sb c.Text
        appendPayload sb c.Cashtag
    | RichText.BotCommand b ->
        appendRichText sb b.Text
        appendPayload sb b.BotCommand
    | RichText.Anchor a -> appendPayload sb a.Name
    | RichText.AnchorLink l ->
        appendRichText sb l.Text
        appendPayload sb l.AnchorName
    | RichText.Reference r ->
        appendRichText sb r.Text
        appendPayload sb r.Name
    | RichText.ReferenceLink l ->
        appendRichText sb l.Text
        appendPayload sb l.ReferenceName

let private appendRichTextLine (sb: StringBuilder) (richText: RichText) =
    appendRichText sb richText
    appendLineBreak sb

let private appendOptionalCredit (sb: StringBuilder) (credit: RichText option) =
    match credit with
    | Some credit -> appendRichTextLine sb credit
    | None -> ()

let private appendCaption (sb: StringBuilder) (caption: RichBlockCaption option) =
    match caption with
    | Some caption ->
        appendRichTextLine sb caption.Text
        appendOptionalCredit sb caption.Credit
    | None -> ()

let rec private appendBlock (sb: StringBuilder) (block: RichBlock) =
    match block with
    | RichBlock.Paragraph p -> appendRichTextLine sb p.Text
    | RichBlock.SectionHeading h -> appendRichTextLine sb h.Text
    | RichBlock.Preformatted p -> appendRichTextLine sb p.Text
    | RichBlock.Footer f -> appendRichTextLine sb f.Text
    | RichBlock.PullQuotation p ->
        appendRichTextLine sb p.Text
        appendOptionalCredit sb p.Credit
    | RichBlock.Thinking t -> appendRichTextLine sb t.Text
    | RichBlock.MathematicalExpression m -> appendPayload sb m.Expression
    | RichBlock.Divider _ -> ()
    | RichBlock.Anchor a -> appendPayload sb a.Name
    | RichBlock.List l ->
        for item in l.Items do
            if not (String.IsNullOrWhiteSpace item.Label) then
                %sb.Append item.Label
                appendLineBreak sb
            for nested in item.Blocks do
                appendBlock sb nested
    | RichBlock.BlockQuotation q ->
        for nested in q.Blocks do
            appendBlock sb nested
        appendOptionalCredit sb q.Credit
    | RichBlock.Collage c ->
        for nested in c.Blocks do
            appendBlock sb nested
        appendCaption sb c.Caption
    | RichBlock.Slideshow s ->
        for nested in s.Blocks do
            appendBlock sb nested
        appendCaption sb s.Caption
    | RichBlock.Table t ->
        for row in t.Cells do
            for cell in row do
                match cell.Text with
                | Some text -> appendRichTextLine sb text
                | None -> ()
        match t.Caption with
        | Some caption -> appendRichTextLine sb caption
        | None -> ()
    // Details content is extracted even when the block is collapsed
    // (IsOpen absent) — that is exactly where spam would hide.
    | RichBlock.Details d ->
        appendRichTextLine sb d.Summary
        for nested in d.Blocks do
            appendBlock sb nested
    | RichBlock.Map m -> appendCaption sb m.Caption
    | RichBlock.Animation a -> appendCaption sb a.Caption
    | RichBlock.Audio a -> appendCaption sb a.Caption
    | RichBlock.Photo p -> appendCaption sb p.Caption
    | RichBlock.Video v -> appendCaption sb v.Caption
    | RichBlock.VoiceNote v -> appendCaption sb v.Caption

/// Flattens all human-visible text and all URL/payload strings of a rich
/// message into newline-separated plain text. Never returns null; may return
/// an empty string for content-free messages (caller checks).
let flatten (richMessage: RichMessage) : string =
    let sb = StringBuilder()
    for block in richMessage.Blocks do
        appendBlock sb block
    sb.ToString().Trim('\r', '\n')
