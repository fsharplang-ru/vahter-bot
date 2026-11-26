/// Pure OCR target-selection logic for Telegram stickers, extracted so it is testable without
/// a running bot (see the 2026-08-12 incident: a caption-less static sticker produced zero
/// ML/OCR/LLM activity — see VahterBanBot.Bot's TgMessage.Sticker/ExternalReplySticker wiring
/// and the hasPendingAzureOcr gate for the rest of the fix).
module VahterBanBot.StickerOcr

open System
open Funogram.Telegram.Types

/// A minimal file reference OCR can act on — abstracts over a sticker's own file vs its
/// Thumbnail (a PhotoSize) so callers don't care which one was picked.
type OcrFileRef =
    { FileId: string
      FileUniqueId: string
      FileSize: int64 option }

let private fitsPixelBudget (width: int64) (height: int64) =
    width > 0L && height > 0L && width <= 1_048_576L / height

/// Prefer the original static image when it fits the decode budget, otherwise use an eligible thumbnail.
let selectOcrTarget (sticker: Sticker) : Result<OcrFileRef, string> =
    let isStatic = not sticker.IsAnimated && not sticker.IsVideo
    if isStatic && fitsPixelBudget sticker.Width sticker.Height then
        Ok { FileId = sticker.FileId; FileUniqueId = sticker.FileUniqueId; FileSize = sticker.FileSize }
    else
        match sticker.Thumbnail with
        | Some thumb when fitsPixelBudget thumb.Width thumb.Height ->
            Ok { FileId = thumb.FileId; FileUniqueId = thumb.FileUniqueId; FileSize = thumb.FileSize }
        | None when not isStatic -> Error "sticker-no-static-representation"
        | _ -> Error "sticker-too-large"

/// Same OcrMaxFileSizeBytes gate photos use (Bot.fs's SelectOcrPhoto) — unknown size (0L /
/// absent) is allowed, same as photos.
let isWithinSizeLimit (maxBytes: int64) (fileRef: OcrFileRef) : bool =
    fileRef.FileSize
    |> Option.map (fun size -> size = 0L || size <= maxBytes)
    |> Option.defaultValue true

/// Combines target selection with the size gate. The size-limit rejection is reported via the
/// same Error channel (a distinct reason string) so callers can tag it on their span too.
let selectEligibleOcrTarget (maxBytes: int64) (sticker: Sticker) : Result<OcrFileRef, string> =
    match selectOcrTarget sticker with
    | Error reason -> Error reason
    | Ok fileRef ->
        if isWithinSizeLimit maxBytes fileRef then Ok fileRef
        else Error "sticker-too-large"

/// Bound WebP decoding to one megapixel before allocating the bitmap.
let prepareReadImage (bytes: byte array) =
    let isWebP =
        bytes.Length >= 12
        && bytes.AsSpan(0, 4).SequenceEqual("RIFF"B)
        && bytes.AsSpan(8, 4).SequenceEqual("WEBP"B)
    if isWebP then
        use data = SkiaSharp.SKData.CreateCopy(bytes)
        use codec = SkiaSharp.SKCodec.Create(data)
        if isNull codec || not (fitsPixelBudget (int64 codec.Info.Width) (int64 codec.Info.Height)) then
            invalidArg (nameof bytes) "Invalid Telegram WebP sticker dimensions"
        use bitmap = SkiaSharp.SKBitmap.Decode(codec)
        if isNull bitmap then invalidArg (nameof bytes) "Invalid WebP sticker"
        use png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100)
        png.ToArray()
    else
        bytes
