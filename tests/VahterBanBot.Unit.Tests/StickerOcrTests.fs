/// Pure unit coverage for VahterBanBot.StickerOcr — the target-selection logic behind the
/// 2026-08-12 fix (caption-less sticker spam invisible to ML/OCR/LLM triage). Ground-truth
/// shape (width/height/file_size, no set_name/emoji) matches the actual prod spam sticker.
module VahterBanBot.Unit.Tests.StickerOcrTests

open BotTestInfra
open Funogram.Telegram.Types
open VahterBanBot.StickerOcr
open Xunit

[<Fact>]
let ``static sticker (not animated, not video): OCR target is the sticker's own file`` () =
    let sticker = Tg.staticSticker(fileId = "sticker-file-id", fileUniqueId = "sticker-file-uid")
    match selectOcrTarget sticker with
    | Ok fileRef ->
        Assert.Equal("sticker-file-id", fileRef.FileId)
        Assert.Equal("sticker-file-uid", fileRef.FileUniqueId)
    | Error reason -> Assert.Fail($"expected Ok, got Error {reason}")

[<Fact>]
let ``animated sticker with a thumbnail: OCR target is the thumbnail, not the animation`` () =
    let sticker =
        Tg.animatedStickerWithThumbnail(
            fileId = "anim-file-id",
            fileUniqueId = "anim-file-uid",
            thumbFileId = "thumb-file-id",
            thumbFileUniqueId = "thumb-file-uid")
    match selectOcrTarget sticker with
    | Ok fileRef ->
        Assert.Equal("thumb-file-id", fileRef.FileId)
        Assert.Equal("thumb-file-uid", fileRef.FileUniqueId)
    | Error reason -> Assert.Fail($"expected Ok, got Error {reason}")

[<Fact>]
let ``animated sticker with no thumbnail: no static representation, skip reason reported`` () =
    let sticker = Tg.animatedStickerNoThumbnail()
    match selectOcrTarget sticker with
    | Error reason -> Assert.Equal("sticker-no-static-representation", reason)
    | Ok _ -> Assert.Fail("expected Error, got Ok")

[<Fact>]
let ``video sticker with no thumbnail: same no-static-representation reason as animated`` () =
    let sticker =
        Sticker.Create(
            fileId = "video-sticker",
            fileUniqueId = "video-sticker-uid",
            ``type`` = "regular",
            width = 512L,
            height = 512L,
            isAnimated = false,
            isVideo = true)
    match selectOcrTarget sticker with
    | Error reason -> Assert.Equal("sticker-no-static-representation", reason)
    | Ok _ -> Assert.Fail("expected Error, got Ok")

[<Fact>]
let ``isWithinSizeLimit: unknown file size (None) is always allowed`` () =
    let fileRef = { FileId = "f"; FileUniqueId = "f-uid"; FileSize = None }
    Assert.True(isWithinSizeLimit 1024L fileRef)

[<Fact>]
let ``isWithinSizeLimit: zero file size is always allowed (matches SelectOcrPhoto's photo rule)`` () =
    let fileRef = { FileId = "f"; FileUniqueId = "f-uid"; FileSize = Some 0L }
    Assert.True(isWithinSizeLimit 1024L fileRef)

[<Fact>]
let ``isWithinSizeLimit: size at or under the limit is allowed`` () =
    let fileRef = { FileId = "f"; FileUniqueId = "f-uid"; FileSize = Some 1024L }
    Assert.True(isWithinSizeLimit 1024L fileRef)

[<Fact>]
let ``isWithinSizeLimit: size over the limit is rejected`` () =
    let fileRef = { FileId = "f"; FileUniqueId = "f-uid"; FileSize = Some 1025L }
    Assert.False(isWithinSizeLimit 1024L fileRef)

[<Fact>]
let ``selectEligibleOcrTarget: oversized static sticker is rejected as sticker-too-large`` () =
    let sticker =
        Sticker.Create(
            fileId = "big-sticker",
            fileUniqueId = "big-sticker-uid",
            ``type`` = "regular",
            width = 512L,
            height = 512L,
            isAnimated = false,
            isVideo = false,
            fileSize = 25L * 1024L * 1024L)
    match selectEligibleOcrTarget (1024L * 1024L) sticker with
    | Error reason -> Assert.Equal("sticker-too-large", reason)
    | Ok _ -> Assert.Fail("expected Error, got Ok")

[<Fact>]
let ``selectEligibleOcrTarget: oversized thumbnail on an animated sticker is also rejected`` () =
    let sticker =
        Sticker.Create(
            fileId = "anim-sticker",
            fileUniqueId = "anim-sticker-uid",
            ``type`` = "regular",
            width = 512L,
            height = 512L,
            isAnimated = true,
            isVideo = false,
            thumbnail = PhotoSize.Create("thumb", "thumb-uid", 320L, 320L, fileSize = 25L * 1024L * 1024L))
    match selectEligibleOcrTarget (1024L * 1024L) sticker with
    | Error reason -> Assert.Equal("sticker-too-large", reason)
    | Ok _ -> Assert.Fail("expected Error, got Ok")

[<Fact>]
let ``selectEligibleOcrTarget: eligible static sticker under the limit is Ok`` () =
    let sticker = Tg.staticSticker()
    match selectEligibleOcrTarget (1024L * 1024L) sticker with
    | Ok _ -> ()
    | Error reason -> Assert.Fail($"expected Ok, got Error {reason}")
