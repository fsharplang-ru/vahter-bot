/// Pure OCR target-selection logic for Telegram stickers, extracted so it is testable without
/// a running bot (see the 2026-08-12 incident: a caption-less static sticker produced zero
/// ML/OCR/LLM activity — see VahterBanBot.Bot's TgMessage.Sticker/ExternalReplySticker wiring
/// and the hasPendingAzureOcr gate for the rest of the fix).
module VahterBanBot.StickerOcr

open Funogram.Telegram.Types

/// A minimal file reference OCR can act on — abstracts over a sticker's own file vs its
/// Thumbnail (a PhotoSize) so callers don't care which one was picked.
type OcrFileRef =
    { FileId: string
      FileUniqueId: string
      FileSize: int64 option }

/// Static stickers (IsAnimated=false && IsVideo=false — regular/mask/custom_emoji WebP) are
/// OCRed directly at full resolution. Animated (TGS) and video (WebM) stickers have no static
/// frame to hand Azure, so we fall back to the sticker's Thumbnail (always a static PhotoSize
/// per the Bot API docs) when Telegram sent one. No thumbnail at all means there is no static
/// representation to OCR — Error carries a stable skip-reason string for span tagging, so the
/// skip stays visible in traces instead of vanishing silently (the exact bug this feature fixes).
let selectOcrTarget (sticker: Sticker) : Result<OcrFileRef, string> =
    if not sticker.IsAnimated && not sticker.IsVideo then
        Ok { FileId = sticker.FileId; FileUniqueId = sticker.FileUniqueId; FileSize = sticker.FileSize }
    else
        match sticker.Thumbnail with
        | Some thumb -> Ok { FileId = thumb.FileId; FileUniqueId = thumb.FileUniqueId; FileSize = thumb.FileSize }
        | None -> Error "sticker-no-static-representation"

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
