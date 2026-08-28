/// Pure unit coverage for `hasSuspiciousApkAttachment` (public in Bot.fs). Detection is
/// filename-only (Telegram never sends the Android mime type); reply_to_message excluded.
module VahterBanBot.Unit.Tests.SuspiciousAttachmentTests

open BotTestInfra
open VahterBanBot
open VahterBanBot.Bot
open Xunit

let private msgOf (update: Funogram.Telegram.Types.Update) = TgMessage.Create(update.Message.Value)
let private apk = ["apk"]

[<Fact>]
let ``own document with .apk filename matches when "apk" is configured`` () =
    let doc = Tg.document(fileName = "malware.apk", mimeType = "application/octet-stream")
    let msg = msgOf (Tg.quickMsg(text = null, document = doc))
    Assert.True(hasSuspiciousApkAttachment apk msg)

[<Fact>]
let ``external_reply document with .apk filename matches (dominant prod pattern)`` () =
    let doc = Tg.document(fileName = "Поиск Пропавших.apk", mimeType = "application/octet-stream")
    let msg = msgOf (Tg.quickMsg(text = null, externalReply = Tg.externalReply(document = doc)))
    Assert.True(hasSuspiciousApkAttachment apk msg)

[<Fact>]
let ``case-insensitive .APK extension matches`` () =
    let doc = Tg.document(fileName = "MALWARE.APK")
    let msg = msgOf (Tg.quickMsg(text = null, document = doc))
    Assert.True(hasSuspiciousApkAttachment apk msg)

[<Fact>]
let ``non-apk document does NOT match`` () =
    let doc = Tg.document(fileName = "resume.pdf", mimeType = "application/pdf")
    let msg = msgOf (Tg.quickMsg(text = null, document = doc))
    Assert.False(hasSuspiciousApkAttachment apk msg)

[<Fact>]
let ``reply_to_message document does NOT match (belongs to a different, earlier sender)`` () =
    let doc = Tg.document(fileName = "malware.apk")
    let chat = Tg.chat()
    let replyTarget = Tg.quickMsg(text = null, chat = chat, document = doc).Message.Value
    let msg = msgOf (Tg.quickMsg(text = "reply text", chat = chat, replyToMessage = replyTarget))
    Assert.False(hasSuspiciousApkAttachment apk msg)

[<Fact>]
let ``null file_name is safe (no document, no crash)`` () =
    let msg = msgOf (Tg.quickMsg(text = "just text"))
    Assert.False(hasSuspiciousApkAttachment apk msg)

[<Fact>]
let ``document with no file_name at all is safe and does not match`` () =
    let doc = Tg.document()
    let msg = msgOf (Tg.quickMsg(text = null, document = doc))
    Assert.False(hasSuspiciousApkAttachment apk msg)

// ── SUSPICIOUS_ATTACHMENT_EXTENSIONS list semantics ─────────────────────────

[<Fact>]
let ``empty extension list disables the heuristic entirely, even for an apk`` () =
    let doc = Tg.document(fileName = "malware.apk")
    let msg = msgOf (Tg.quickMsg(text = null, document = doc))
    Assert.False(hasSuspiciousApkAttachment [] msg)

[<Fact>]
let ``multi-extension list matches any of its entries`` () =
    let extensions = ["apk"; "html"]
    let apkMsg = msgOf (Tg.quickMsg(text = null, document = Tg.document(fileName = "a.apk")))
    let htmlMsg = msgOf (Tg.quickMsg(text = null, document = Tg.document(fileName = "b.html")))
    let pdfMsg = msgOf (Tg.quickMsg(text = null, document = Tg.document(fileName = "c.pdf")))
    Assert.True(hasSuspiciousApkAttachment extensions apkMsg)
    Assert.True(hasSuspiciousApkAttachment extensions htmlMsg)
    Assert.False(hasSuspiciousApkAttachment extensions pdfMsg)

[<Fact>]
let ``extension stored with a leading dot still matches (normalized on read)`` () =
    let doc = Tg.document(fileName = "malware.apk")
    let msg = msgOf (Tg.quickMsg(text = null, document = doc))
    Assert.True(hasSuspiciousApkAttachment [".apk"] msg)

[<Fact>]
let ``extension stored in mixed case / with whitespace still matches (normalized on read)`` () =
    let doc = Tg.document(fileName = "malware.apk")
    let msg = msgOf (Tg.quickMsg(text = null, document = doc))
    Assert.True(hasSuspiciousApkAttachment [" APK "] msg)
