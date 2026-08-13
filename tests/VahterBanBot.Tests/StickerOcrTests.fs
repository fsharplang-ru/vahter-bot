module VahterBanBot.Tests.StickerOcrTests

open Dapper
open Npgsql
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

/// Regression coverage for the 2026-08-12 incident: a caption-less STATIC sticker spam message
/// produced zero ML/OCR/LLM activity (msg.Text = null never satisfied the ML gate). See
/// VahterBanBot.Bot's TgMessage.Sticker/ExternalReplySticker wiring, hasPendingAzureOcr, and
/// VahterBanBot.StickerOcr.selectEligibleOcrTarget for the fix.
///
/// Test names start with "Sticker" so they sort with the rest of the OCR suite; same
/// MlEnabledVahterTestContainers fixture as OcrCacheTests/OcrShortCircuitTests — assembly-wide
/// DisableTestParallelization=true guarantees serial execution, so Azure call-count deltas are
/// deterministic.
type StickerOcrTests(fixture: MlEnabledVahterTestContainers, _ml: MlAwaitFixture) =

    let seedOcrCache (fileUniqueId: string) (extractedText: string) = task {
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        //language=postgresql
        let sql =
            """
INSERT INTO ocr_cache (file_unique_id, azure_response, extracted_text)
VALUES (@fileUniqueId, '{}'::jsonb, @extractedText)
ON CONFLICT (file_unique_id) DO UPDATE SET extracted_text = EXCLUDED.extracted_text
            """
        let! _ = conn.ExecuteAsync(sql, {| fileUniqueId = fileUniqueId; extractedText = extractedText |})
        return ()
    }

    [<Fact>]
    let ``Caption-less static sticker with spam OCR text is classified and deleted`` () = task {
        // The exact prod bug: no caption, so msg.Text is null at the ML gate. Without the
        // hasPendingAzureOcr fix, this message never reaches ML/OCR/LLM at all.
        do! fixture.SetOcrText "2222222"
        let sticker = Tg.staticSticker()

        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = null, sticker = sticker)
        let! _ = fixture.SendMessage msg

        let! deleted = fixture.MessageIsAutoDeleted msg.Message.Value
        Assert.True(deleted, "Caption-less sticker with spam OCR text must be auto-deleted")

        let! db = fixture.TryGetDbMessage msg.Message.Value
        Assert.True(db.IsSome, "Message should be recorded")
        Assert.Contains("2222222", db.Value.text)
    }

    [<Fact>]
    let ``Sticker OCR result is cached: identical sticker on a second message hits cache`` () = task {
        let sticker = Tg.staticSticker()

        do! fixture.SetOcrText "STICKER-CACHE-TEST-MARKER"

        let! beforeCount = fixture.GetAzureOcrAnalyzeCallCount()

        // First send — cache miss, MUST trigger one Azure analyze call.
        let msg1 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = null, sticker = sticker)
        let! _ = fixture.SendMessage msg1

        let! db1 = fixture.TryGetDbMessage msg1.Message.Value
        Assert.True(db1.IsSome, "First message should be recorded after enrichment")
        Assert.Contains("STICKER-CACHE-TEST-MARKER", db1.Value.text)

        let! afterFirstCount = fixture.GetAzureOcrAnalyzeCallCount()
        Assert.Equal(beforeCount + 1, afterFirstCount)

        // Second send — same sticker (same FileUniqueId), MUST hit cache, skip Azure entirely.
        let msg2 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = null, sticker = sticker)
        let! _ = fixture.SendMessage msg2

        let! db2 = fixture.TryGetDbMessage msg2.Message.Value
        Assert.True(db2.IsSome, "Second message should be recorded after cache-hit enrichment")
        Assert.Contains("STICKER-CACHE-TEST-MARKER", db2.Value.text)

        let! afterSecondCount = fixture.GetAzureOcrAnalyzeCallCount()
        Assert.Equal(afterFirstCount, afterSecondCount)
    }

    [<Fact>]
    let ``Animated sticker with a thumbnail: OCR runs against the thumbnail, not the animation`` () = task {
        let sticker = Tg.animatedStickerWithThumbnail()
        let thumbFileUniqueId = sticker.Thumbnail.Value.FileUniqueId

        // Seed the cache under the THUMBNAIL's file_unique_id (not the sticker's own) — if the
        // code targeted the wrong file, this seed would be irrelevant and Azure would be called.
        do! seedOcrCache thumbFileUniqueId "2222222"

        let! before = fixture.GetAzureOcrAnalyzeCallCount()

        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = null, sticker = sticker)
        let! _ = fixture.SendMessage msg

        let! deleted = fixture.MessageIsAutoDeleted msg.Message.Value
        Assert.True(deleted, "Thumbnail cache-hit OCR text must classify the animated sticker as spam")

        let! after = fixture.GetAzureOcrAnalyzeCallCount()
        Assert.Equal(before, after)
    }

    [<Fact>]
    let ``Animated sticker without a thumbnail is skipped without error`` () = task {
        do! fixture.SetOcrText "2222222"  // would mark spam if (wrongly) reached
        let sticker = Tg.animatedStickerNoThumbnail()

        let! before = fixture.GetAzureOcrAnalyzeCallCount()

        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = null, sticker = sticker)
        let! resp = fixture.SendMessage msg

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode)

        // No static representation to OCR — Azure must never be called for this sticker.
        let! after = fixture.GetAzureOcrAnalyzeCallCount()
        Assert.Equal(before, after)

        let! deleted = fixture.MessageIsAutoDeleted msg.Message.Value
        Assert.False(deleted, "No OCR text and no caption — nothing to classify as spam")

        // Message is still recorded — the skip must not block the rest of the pipeline.
        let! db = fixture.TryGetDbMessage msg.Message.Value
        Assert.True(db.IsSome, "Message should be recorded even when sticker OCR is skipped")
    }

    interface IClassFixture<MlAwaitFixture>
