module VahterBanBot.Tests.OcrCacheTests

open Telegram.Bot.Types
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

/// OCR cache uses MlEnabledVahterTestContainers (OCR_ENABLED=true).
/// Test names start with "OCR cache" — sorts after "LLM" (LlmTriageTests) and before
/// "P" (PingTests etc.). Same-class fixture; assembly-wide DisableTestParallelization=true
/// guarantees serial execution, so Azure call-count deltas are deterministic.
type OcrCacheTests(fixture: MlEnabledVahterTestContainers, _ml: MlAwaitFixture) =

    [<Fact>]
    let ``OCR cache: same photo file_unique_id is OCRed only once`` () = task {
        let chat = fixture.ChatsToMonitor[0]

        // Stable photo identity: cache key is FileUniqueId.
        let testPhoto =
            PhotoSize(
                FileId = "ocr-cache-test",
                FileUniqueId = "ocr-cache-test-uid",
                FileSize = 1024,
                Width = 10,
                Height = 10
            )

        do! fixture.SetOcrText "OCR-CACHE-TEST-MARKER"

        let! beforeCount = fixture.GetAzureOcrAnalyzeCallCount()

        // First send — cache miss, MUST trigger one Azure analyze call.
        let msg1 = Tg.quickMsg(chat = chat, text = null, photos = [|testPhoto|])
        let! _ = fixture.SendMessage msg1

        let! db1 = fixture.TryGetDbMessage msg1.Message
        Assert.True(db1.IsSome, "First message should be recorded after enrichment")
        Assert.Contains("OCR-CACHE-TEST-MARKER", db1.Value.text)

        let! afterFirstCount = fixture.GetAzureOcrAnalyzeCallCount()
        Assert.Equal(beforeCount + 1, afterFirstCount)

        // Second send — same FileUniqueId, MUST hit cache and skip Azure entirely.
        let msg2 = Tg.quickMsg(chat = chat, text = null, photos = [|testPhoto|])
        let! _ = fixture.SendMessage msg2

        let! db2 = fixture.TryGetDbMessage msg2.Message
        Assert.True(db2.IsSome, "Second message should be recorded after cache-hit enrichment")
        Assert.Contains("OCR-CACHE-TEST-MARKER", db2.Value.text)

        let! afterSecondCount = fixture.GetAzureOcrAnalyzeCallCount()
        Assert.Equal(afterFirstCount, afterSecondCount)
    }

    interface IClassFixture<MlAwaitFixture>
