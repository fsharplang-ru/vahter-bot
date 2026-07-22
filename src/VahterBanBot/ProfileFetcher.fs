module VahterBanBot.ProfileFetcher

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Funogram.Telegram.Types
open VahterBanBot
open BotInfra

module Req = Funogram.Telegram.Req

/// Fetched user profile data used by reaction-spam triage. Either field can be empty:
/// privacy-strict users return no photo and an empty bio. Don't ban based on absence.
type UserProfile =
    { PhotoBytes: byte[] option
      Bio:        string }

type IUserProfileFetcher =
    /// Returns the profile from cache if fetched within the TTL, otherwise calls Telegram and updates the cache.
    /// On any Telegram error, returns an empty profile (None photo, empty bio) — never throws.
    abstract member Fetch: userId: int64 -> Task<UserProfile>

type UserProfileFetcher(tg: ITelegramApi, db: DbService, logger: ILogger<UserProfileFetcher>) =
    let cacheTtl = TimeSpan.FromDays 7.0

    let pickLargestPhoto (photos: PhotoSize[][]) =
        if isNull photos || photos.Length = 0 then None
        else
            let firstPhoto = photos[0]
            if isNull firstPhoto || firstPhoto.Length = 0 then None
            else firstPhoto |> Array.maxBy (fun p -> p.Width * p.Height) |> Some

    let downloadPhoto (fileId: string) = task {
        try
            let! file = tg.CallExn(Req.GetFile.Make fileId)
            match file.FilePath with
            | Some filePath ->
                let! bytes = tg.DownloadFile filePath
                return Some bytes
            | None ->
                logger.LogWarning("No file path resolved for profile photo {FileId}", fileId)
                return None
        with ex ->
            logger.LogWarning(ex, "Failed to download profile photo {FileId}", fileId)
            return None
    }

    interface IUserProfileFetcher with
        member _.Fetch(userId: int64) = task {
            match! db.GetCachedUserProfile(userId, cacheTtl) with
            | Some cached ->
                let photo = if isNull cached.photo_bytes || cached.photo_bytes.Length = 0 then None else Some cached.photo_bytes
                let bio   = if isNull cached.bio then "" else cached.bio
                return { PhotoBytes = photo; Bio = bio }
            | None ->
                // Bio: GetChat(userId) returns a ChatFullInfo; private-chat-style call works for users.
                let! bio =
                    task {
                        try
                            let! chat = tg.CallExn(Req.GetChat.Make userId)
                            return defaultArg chat.Bio ""
                        with ex ->
                            logger.LogInformation(ex, "GetChat failed for user {UserId} (likely privacy-strict)", userId)
                            return ""
                    }

                // Photo: largest size of the most recent user profile photo.
                let! photoBytes =
                    task {
                        try
                            let! photos = tg.CallExn(Req.GetUserProfilePhotos.Make(userId, limit = 1L))
                            match pickLargestPhoto photos.Photos with
                            | None -> return None
                            | Some largest -> return! downloadPhoto largest.FileId
                        with ex ->
                            logger.LogInformation(ex, "GetUserProfilePhotos failed for user {UserId}", userId)
                            return None
                    }

                do! db.UpsertUserProfile(userId, photoBytes, bio)
                return { PhotoBytes = photoBytes; Bio = bio }
        }
