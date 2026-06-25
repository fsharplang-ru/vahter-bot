module VahterBanBot.ProfileFetcher

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Telegram.Bot
open VahterBanBot

/// Fetched user profile data used by reaction-spam triage. Either field can be empty:
/// privacy-strict users return no photo and an empty bio. Don't ban based on absence.
type UserProfile =
    { PhotoBytes: byte[] option
      Bio:        string }

type IUserProfileFetcher =
    /// Returns the profile from cache if fetched within the TTL, otherwise calls Telegram and updates the cache.
    /// On any Telegram error, returns an empty profile (None photo, empty bio) — never throws.
    abstract member Fetch: userId: int64 -> Task<UserProfile>

type UserProfileFetcher(botClient: ITelegramBotClient, db: DbService, logger: ILogger<UserProfileFetcher>) =
    let cacheTtl = TimeSpan.FromDays 7.0

    let pickLargestPhoto (photos: Telegram.Bot.Types.PhotoSize[][]) =
        if isNull photos || photos.Length = 0 then None
        else
            let firstPhoto = photos[0]
            if isNull firstPhoto || firstPhoto.Length = 0 then None
            else firstPhoto |> Array.maxBy (fun p -> p.Width * p.Height) |> Some

    let downloadPhoto (fileId: string) = task {
        try
            let! file = botClient.GetFile(fileId)
            use ms = new MemoryStream()
            do! botClient.DownloadFile(file.FilePath, ms)
            return Some (ms.ToArray())
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
                            let! chat = botClient.GetChat(Telegram.Bot.Types.ChatId userId)
                            let raw = chat.Bio
                            return (if isNull raw then "" else raw)
                        with ex ->
                            logger.LogInformation(ex, "GetChat failed for user {UserId} (likely privacy-strict)", userId)
                            return ""
                    }

                // Photo: largest size of the most recent user profile photo.
                let! photoBytes =
                    task {
                        try
                            let! photos = botClient.GetUserProfilePhotos(userId, limit = 1)
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
