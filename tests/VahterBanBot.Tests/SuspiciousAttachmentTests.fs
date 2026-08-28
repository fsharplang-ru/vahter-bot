module VahterBanBot.Tests.SuspiciousAttachmentTests

open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

/// Integration coverage for hasSuspiciousApkAttachment (SUSPICIOUS_ATTACHMENT_EXTENSIONS) and
/// the document triage-gate fix. Setting defaults to "[]" (off) — each apk test seeds/resets it.
type SuspiciousAttachmentTests(fixture: MlEnabledVahterTestContainers, _ml: MlAwaitFixture) =

    let setApkExtensions (json: string) = task {
        do! fixture.SetBotSetting("SUSPICIOUS_ATTACHMENT_EXTENSIONS", json)
        do! fixture.ReloadSettings()
    }

    [<Fact>]
    let ``Own document with .apk filename is deleted with reason SuspiciousAttachment when "apk" is seeded`` () = task {
        do! setApkExtensions """["apk"]"""
        let doc = Tg.document(fileName = "malware.apk", mimeType = "application/octet-stream")
        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = null, document = doc)
        let! _ = fixture.SendMessage msg

        let! deleted = fixture.MessageIsAutoDeleted msg.Message.Value
        Assert.True(deleted, "apk document must be auto-deleted")

        let! reasonCase = fixture.TryGetAutoDeleteReasonCase msg.Message.Value
        Assert.Equal(Some "SuspiciousAttachment", reasonCase)

        do! setApkExtensions "[]"
    }

    [<Fact>]
    let ``External-reply quoted document with .apk filename is deleted with reason SuspiciousAttachment (dominant prod pattern)`` () = task {
        do! setApkExtensions """["apk"]"""
        let doc = Tg.document(fileName = "Поиск Пропавших.apk", mimeType = "application/octet-stream")
        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = null, externalReply = Tg.externalReply(document = doc))
        let! _ = fixture.SendMessage msg

        let! deleted = fixture.MessageIsAutoDeleted msg.Message.Value
        Assert.True(deleted, "external-reply apk document must be auto-deleted")

        let! reasonCase = fixture.TryGetAutoDeleteReasonCase msg.Message.Value
        Assert.Equal(Some "SuspiciousAttachment", reasonCase)

        do! setApkExtensions "[]"
    }

    [<Fact>]
    let ``Unseeded (default empty) extension list leaves an apk document undeleted`` () = task {
        let doc = Tg.document(fileName = "malware.apk", mimeType = "application/octet-stream")
        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = null, document = doc)
        let! _ = fixture.SendMessage msg

        let! deleted = fixture.MessageIsAutoDeleted msg.Message.Value
        Assert.False(deleted, "heuristic must be off when SUSPICIOUS_ATTACHMENT_EXTENSIONS is unseeded")
    }

    [<Fact>]
    let ``Caption-less non-apk document is ML-scored instead of being silently dropped`` () = task {
        let doc = Tg.document(fileName = "resume.pdf", mimeType = "application/pdf", fileSize = 51200L)
        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = null, document = doc)
        let! _ = fixture.SendMessage msg

        let! db = fixture.TryGetDbMessage msg.Message.Value
        Assert.True(db.IsSome, "document-only message must still be recorded")

        let! mlScore = fixture.GetMlScore msg.Message.Value
        Assert.True(mlScore.IsSome, "document-only null-text message must reach ML scoring, not be silently skipped")
    }

    interface IClassFixture<MlAwaitFixture>
