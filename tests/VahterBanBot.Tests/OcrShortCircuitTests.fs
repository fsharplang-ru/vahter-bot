module VahterBanBot.Tests.OcrShortCircuitTests

open System.Net
open Dapper
open Npgsql
open Telegram.Bot.Types
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

/// Tests for the pre-OCR short-circuit and cache-only OCR enrichment behaviour.
///
/// The bot's OCR pipeline now has three cost tiers:
///   1. Cache-hit OCR text feeds the pre-OCR ML check (no Azure call).
///   2. If text alone (caption + quote + inline keyboard + cache-hit OCR)
///      is decisively spam, classification short-circuits — no Azure call.
///   3. Only when text is inconclusive does Azure OCR run for cache misses.
///
/// These tests verify (a) Azure is skipped when text alone is enough,
/// (b) cache hits and inline keyboard text contribute to that decision,
/// (c) borderline cases still reach Azure, and
/// (d) failures at any OCR stage never block ML classification.
type OcrShortCircuitTests(fixture: MlEnabledVahterTestContainers, _ml: MlAwaitFixture) =

    /// Photo helper — each test uses a unique FileUniqueId to avoid colliding
    /// with the shared serial-test ocr_cache.
    let photo (uid: string) =
        PhotoSize(
            FileId = uid,
            FileUniqueId = uid,
            FileSize = 1024,
            Width = 10,
            Height = 10
        )

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
    let ``Spam caption short-circuits before Azure OCR (cache miss)`` () = task {
        // Cache-miss photo + spammy caption ("2222222" trips ML threshold).
        // The pre-OCR ML check fires on the caption alone — Azure must NOT be called.
        do! fixture.SetOcrText "ham-text"  // would mark message ham if reached
        let p = photo "preocr-cache-miss-uid"

        let! before = fixture.GetAzureOcrAnalyzeCallCount()

        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "2222222", photos = [|p|])
        let! _ = fixture.SendMessage msg

        let! deleted = fixture.MessageIsAutoDeleted msg.Message
        Assert.True(deleted, "Spam caption should still trigger auto-delete")

        let! after = fixture.GetAzureOcrAnalyzeCallCount()
        Assert.Equal(before, after)
    }

    [<Fact>]
    let ``Cache-hit OCR text is enough to short-circuit Azure`` () = task {
        // Seed cache with spam text for this photo. Caption is benign on its own,
        // but combined with the cache-hit OCR text it crosses the ML threshold.
        let p = photo "cachehit-spam-uid"
        do! seedOcrCache p.FileUniqueId "2222222"

        let! before = fixture.GetAzureOcrAnalyzeCallCount()

        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "hi", photos = [|p|])
        let! _ = fixture.SendMessage msg

        let! deleted = fixture.MessageIsAutoDeleted msg.Message
        Assert.True(deleted, "Cache-hit OCR text should make the combined text trip the ML threshold")

        let! after = fixture.GetAzureOcrAnalyzeCallCount()
        Assert.Equal(before, after)

        // Sanity: the cache-hit text is what landed on the message
        let! db = fixture.TryGetDbMessage msg.Message
        Assert.True(db.IsSome)
        Assert.Contains("2222222", db.Value.text)
    }

    [<Fact>]
    let ``Inline keyboard text alone short-circuits Azure`` () = task {
        // Spammy inline keyboard text + cache-miss photo.
        // Pre-OCR check fires on caption + keyboard text; Azure must not run.
        do! fixture.SetOcrText "ham-text"
        let p = photo "inline-kb-shortcircuit-uid"

        let! before = fixture.GetAzureOcrAnalyzeCallCount()

        let msg =
            Tg.quickMsg(
                chat = fixture.ChatsToMonitor[0],
                text = "hello",
                photos = [|p|],
                replyMarkup = Tg.inlineKeyboard [ ("2222222", None) ]
            )
        let! _ = fixture.SendMessage msg

        let! deleted = fixture.MessageIsAutoDeleted msg.Message
        Assert.True(deleted, "Spammy inline keyboard text should trigger short-circuit")

        let! after = fixture.GetAzureOcrAnalyzeCallCount()
        Assert.Equal(before, after)
    }

    [<Fact>]
    let ``External-reply photo cache hit short-circuits Azure`` () = task {
        // Seed cache for the external-reply photo with spammy text.
        // Reply with a benign caption to a forwarded photo; the cache enrichment
        // adds the spam text to msg via PrependText, pre-OCR check fires.
        let p = photo "extreply-cachehit-uid"
        do! seedOcrCache p.FileUniqueId "2222222"

        let! before = fixture.GetAzureOcrAnalyzeCallCount()

        let msg =
            Tg.quickMsg(
                chat = fixture.ChatsToMonitor[0],
                text = "hello",
                externalReply = Tg.externalReply(photos = [|p|])
            )
        let! _ = fixture.SendMessage msg

        let! deleted = fixture.MessageIsAutoDeleted msg.Message
        Assert.True(deleted, "External-reply photo cache hit should trigger pre-OCR short-circuit")

        let! after = fixture.GetAzureOcrAnalyzeCallCount()
        Assert.Equal(before, after)
    }

    [<Fact>]
    let ``Benign caption + cache-miss photo runs Azure as fallback`` () = task {
        // Pre-OCR check returns None (benign text), then deferred Azure runs.
        // This is the regression test for the "deferred OCR" path — make sure
        // Azure still runs when text alone wasn't enough.
        do! fixture.SetOcrText "2222222"  // Azure returns spammy text
        let p = photo "benign-cache-miss-azure-uid"

        let! before = fixture.GetAzureOcrAnalyzeCallCount()

        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "hello world", photos = [|p|])
        let! _ = fixture.SendMessage msg

        let! deleted = fixture.MessageIsAutoDeleted msg.Message
        Assert.True(deleted, "Benign caption + spam OCR text should auto-delete via the deferred Azure path")

        let! after = fixture.GetAzureOcrAnalyzeCallCount()
        Assert.Equal(before + 1, after)
    }

    [<Fact>]
    let ``Photo-only message with cache miss still classified via Azure`` () = task {
        // Bare photo, no caption — msg.Text is null at the gate. The new gate
        // (`MlEnabled && (msg.Text <> null || hasPendingAzureOcr)`) must let
        // these through. Without the gate fix, the message would skip
        // classification entirely.
        do! fixture.SetOcrText "2222222"
        let p = photo "photoonly-cachemiss-uid"

        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = null, photos = [|p|])
        let! _ = fixture.SendMessage msg

        let! deleted = fixture.MessageIsAutoDeleted msg.Message
        Assert.True(deleted, "Photo-only spam message must still be classified despite null caption")
    }

    [<Fact>]
    let ``Azure OCR error does not block the pipeline`` () = task {
        // Pre-OCR check is inconclusive (benign caption), Azure is called for
        // the cache-miss photo and returns 500. The error must be swallowed,
        // GetAutoVerdict must still run, and the bot must respond 200 OK.
        do! fixture.SetAzureOcrResponse(500, "internal error")
        let p = photo "azure-500-uid"

        let msg = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "hello world", photos = [|p|])
        let! resp = fixture.SendMessage msg

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        // Benign text + failed OCR → no spam decision → not deleted, no exception escaped.
        let! deleted = fixture.MessageIsAutoDeleted msg.Message
        Assert.False(deleted, "Benign message should not be deleted when Azure fails")

        // Message should still be recorded in DB (classification reached the recording step).
        let! db = fixture.TryGetDbMessage msg.Message
        Assert.True(db.IsSome, "Message should be recorded even when Azure OCR fails")
    }

    [<Fact>]
    let ``Quote text survives external-reply OCR failure`` () = task {
        // Reproduces the original prod bug: with the previous shared-try
        // structure, an external-reply OCR throw would lose the quote text.
        // Now the quote is committed in OnUpdate before any OCR runs.
        do! fixture.SetAzureOcrResponse(500, "internal error")
        let p = photo "quote-survives-azure-fail-uid"

        let msg =
            Tg.quickMsg(
                chat = fixture.ChatsToMonitor[0],
                text = "hello",
                quote = Tg.textQuote "this-quote-must-survive",
                externalReply = Tg.externalReply(photos = [|p|])
            )
        let! resp = fixture.SendMessage msg

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let! db = fixture.TryGetDbMessage msg.Message
        Assert.True(db.IsSome, "Message should be recorded")
        Assert.Contains("this-quote-must-survive", db.Value.text)
        Assert.Contains("hello", db.Value.text)
    }

    [<Fact>]
    let ``Spammy quote text alone short-circuits Azure`` () = task {
        // Quote text feeds the pre-OCR check; combined with cache-miss photo,
        // Azure must still be skipped because the spam decision is made on
        // quote+caption alone.
        do! fixture.SetOcrText "ham-text"
        let p = photo "quote-spam-shortcircuit-uid"

        let! before = fixture.GetAzureOcrAnalyzeCallCount()

        let msg =
            Tg.quickMsg(
                chat = fixture.ChatsToMonitor[0],
                text = "hello",
                quote = Tg.textQuote "2222222",
                photos = [|p|]
            )
        let! _ = fixture.SendMessage msg

        let! deleted = fixture.MessageIsAutoDeleted msg.Message
        Assert.True(deleted, "Spammy quote text should trigger pre-OCR short-circuit")

        let! after = fixture.GetAzureOcrAnalyzeCallCount()
        Assert.Equal(before, after)
    }

    interface IClassFixture<MlAwaitFixture>
