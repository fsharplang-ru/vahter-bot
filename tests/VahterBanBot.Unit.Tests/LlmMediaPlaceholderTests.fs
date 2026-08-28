/// Pure unit coverage for `LlmTriage.mediaPlaceholder` / `hasStableTextCacheKey` / `formatBioLine`
/// — extracted specifically so the 2026-08-18 empty-text-media incident fix (@AvaloniaRU msg
/// 217142: an innocent caption-less sticker's blank `Message:` body let gpt-4o-mini judge SPAM
/// on sender signals alone) is unit-testable without a live Azure client. See LlmTriage.fs's
/// "Empty-text media placeholder" section for the full incident/cache-key writeup.
module VahterBanBot.Unit.Tests.LlmMediaPlaceholderTests

open BotTestInfra
open Funogram.Telegram.Types
open VahterBanBot
open VahterBanBot.LlmTriage
open Xunit

let private msgOf (update: Funogram.Telegram.Types.Update) = TgMessage.Create(update.Message.Value)

[<Fact>]
let ``sticker with no emoji and no set_name (2026-08-12 prod spam sticker shape): generic placeholder`` () =
    let sticker = Tg.staticSticker()  // no emoji/set_name — see StickerOcrTests.fs's doc comment
    let msg = msgOf (Tg.quickMsg(sticker = sticker, text = null))
    Assert.Equal(Some "[sticker, no readable text]", mediaPlaceholder msg)

[<Fact>]
let ``sticker with emoji and set_name: placeholder names both`` () =
    let sticker =
        Sticker.Create(
            fileId = "cat-sticker", fileUniqueId = "cat-sticker-uid", ``type`` = "regular",
            width = 512L, height = 512L, isAnimated = false, isVideo = false,
            emoji = "🐈‍⬛️", setName = "catssenseoflife")
    let msg = msgOf (Tg.quickMsg(sticker = sticker, text = null))
    Assert.Equal(Some "[sticker \"🐈‍⬛️\" from set \"catssenseoflife\", no readable text]", mediaPlaceholder msg)

[<Fact>]
let ``sticker with emoji only (no set_name): placeholder degrades gracefully`` () =
    let sticker =
        Sticker.Create(
            fileId = "s", fileUniqueId = "s-uid", ``type`` = "regular",
            width = 512L, height = 512L, isAnimated = false, isVideo = false,
            emoji = "😀")
    let msg = msgOf (Tg.quickMsg(sticker = sticker, text = null))
    Assert.Equal(Some "[sticker \"😀\", no readable text]", mediaPlaceholder msg)

[<Fact>]
let ``photo with no OCR text: generic photo placeholder`` () =
    let msg = msgOf (Tg.quickMsg(text = null, photos = [| Tg.spamPhoto |]))
    Assert.Equal(Some "[photo, no readable text]", mediaPlaceholder msg)

[<Fact>]
let ``document with file_name, mime_type and file_size: full placeholder`` () =
    let doc = Tg.document(fileName = "Поиск Пропавших.apk", mimeType = "application/octet-stream", fileSize = 163_840L)
    let msg = msgOf (Tg.quickMsg(text = null, document = doc))
    Assert.Equal(Some "[document: Поиск Пропавших.apk, application/octet-stream, 160 KB]", mediaPlaceholder msg)

[<Fact>]
let ``document with only file_name (mime/size absent): placeholder degrades gracefully`` () =
    let doc = Tg.document(fileName = "resume.pdf")
    let msg = msgOf (Tg.quickMsg(text = null, document = doc))
    Assert.Equal(Some "[document: resume.pdf]", mediaPlaceholder msg)

[<Fact>]
let ``document with no name/mime/size at all: falls back to the generic placeholder`` () =
    let doc = Tg.document()
    let msg = msgOf (Tg.quickMsg(text = null, document = doc))
    Assert.Equal(Some "[document, no readable text]", mediaPlaceholder msg)

[<Fact>]
let ``external_reply document is rendered the same as an own document`` () =
    let doc = Tg.document(fileName = "invoice.exe", mimeType = "application/x-msdownload", fileSize = 2048L)
    let msg = msgOf (Tg.quickMsg(text = null, externalReply = Tg.externalReply(document = doc)))
    Assert.Equal(Some "[document: invoice.exe, application/x-msdownload, 2 KB]", mediaPlaceholder msg)

[<Fact>]
let ``truly empty message (no text, no media): empty-message placeholder`` () =
    let msg = msgOf (Tg.quickMsg(text = null))
    Assert.Equal(Some "[empty message]", mediaPlaceholder msg)

[<Fact>]
let ``message with real text: no placeholder, caller uses msg.Text as-is`` () =
    let msg = msgOf (Tg.quickMsg(text = "buy crypto now"))
    Assert.Equal(None, mediaPlaceholder msg)

[<Fact>]
let ``mediaPlaceholder never mutates msg.Text — it stays null for a sticker-only message`` () =
    // Critical invariant (see LlmTriage.fs's module doc comment above mediaPlaceholder): the
    // placeholder must exist ONLY in the LLM prompt string. msg.Text also feeds the ML scorer,
    // spam-text cache, verdict-cache key, and the deleted-spam channel post — if the placeholder
    // ever mutated msg.Text, every photo/sticker would collapse onto ONE cache key and (since
    // SPAM/SKIP is cached globally by text hash) one verdict would condemn every future photo.
    let sticker = Tg.staticSticker()
    let msg = msgOf (Tg.quickMsg(sticker = sticker, text = null))
    let before = msg.Text
    Assert.Null(before)
    let placeholder = mediaPlaceholder msg
    Assert.True(placeholder.IsSome)
    Assert.Null(msg.Text)                 // unchanged after computing the placeholder
    Assert.Null(msg.OriginalText)         // and the original wire text is untouched too

[<Fact>]
let ``hasStableTextCacheKey: true when msg.Text is non-empty`` () =
    let msg = msgOf (Tg.quickMsg(text = "buy crypto now"))
    Assert.True(hasStableTextCacheKey msg)

[<Fact>]
let ``hasStableTextCacheKey: false for an empty-text message (photo-only)`` () =
    let msg = msgOf (Tg.quickMsg(text = null, photos = [| Tg.spamPhoto |]))
    Assert.False(hasStableTextCacheKey msg)

[<Fact>]
let ``sticker-only message that gets a rendered placeholder still has no stable text cache key (NoCache branch)`` () =
    // Proves the placeholder-rendering change does NOT widen what Classify treats as cacheable:
    // a message that gets a non-None mediaPlaceholder (because msg.Text is empty) must still
    // report false here, so Classify's `if hasStableTextCacheKey msg then ... else NoCache`
    // routes it to NoCache exactly as before this change.
    let sticker = Tg.staticSticker()
    let msg = msgOf (Tg.quickMsg(sticker = sticker, text = null))
    Assert.True((mediaPlaceholder msg).IsSome)
    Assert.False(hasStableTextCacheKey msg)

[<Fact>]
let ``formatBioLine: null/empty/whitespace bio renders as (none)`` () =
    Assert.Equal("(none)", formatBioLine null)
    Assert.Equal("(none)", formatBioLine "")
    Assert.Equal("(none)", formatBioLine "   ")

[<Fact>]
let ``formatBioLine: a real bio is rendered verbatim`` () =
    Assert.Equal("Зайди в мой био", formatBioLine "Зайди в мой био")
