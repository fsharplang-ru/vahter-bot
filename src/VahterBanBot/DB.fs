module VahterBanBot.DB

open System
open System.Threading.Tasks
open Npgsql
open VahterBanBot.Types
open Dapper
open VahterBanBot.Utils

let private connString = getEnv "DATABASE_URL"

let upsertUser (user: DbUser): Task<DbUser> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
INSERT INTO "user" (id, username, ban_reason, banned_at, banned_by, created_at, updated_at)
VALUES (@id, @username, @banReason, @bannedAt, @bannedBy, @createdAt, @updatedAt)
ON CONFLICT (id) DO UPDATE
    SET username   =
            CASE
                WHEN EXCLUDED.username IS DISTINCT FROM "user".username
                    THEN COALESCE(EXCLUDED.username, "user".username)
                ELSE "user".username
                END,
        ban_reason =
            CASE
                WHEN EXCLUDED.ban_reason IS DISTINCT FROM "user".ban_reason
                    THEN COALESCE(EXCLUDED.ban_reason, "user".ban_reason)
                ELSE "user".ban_reason
                END,
        banned_at  =
            CASE
                WHEN EXCLUDED.banned_at IS DISTINCT FROM "user".banned_at
                    THEN COALESCE(EXCLUDED.banned_at, "user".banned_at)
                ELSE "user".banned_at
                END,
        banned_by  =
            CASE
                WHEN EXCLUDED.banned_by IS DISTINCT FROM "user".banned_by
                    THEN COALESCE(EXCLUDED.banned_by, "user".banned_by)
                ELSE "user".banned_by
                END,
        updated_at = GREATEST(EXCLUDED.updated_at, "user".updated_at)
RETURNING *;
"""

        let! insertedUser =
            conn.QueryAsync<DbUser>(
                sql,
                {| id = user.Id
                   username = user.Username
                   banReason = user.Ban_Reason
                   bannedAt = user.Banned_At
                   bannedBy = user.Banned_By
                   createdAt = user.Created_At
                   updatedAt = user.Updated_At |}
            )

        return insertedUser |> Seq.head
    }

let insertMessage (message: DbMessage): Task<DbMessage> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
INSERT INTO message (chat_id, message_id, user_id, created_at)
VALUES (@chatId, @messageId, @userId, @createdAt)
ON CONFLICT (chat_id, message_id) DO NOTHING RETURNING *;
            """

        let! insertedMessage =
            conn.QueryAsync<DbMessage>(
                sql,
                {| chatId = message.Chat_Id
                   messageId = message.Message_Id
                   userId = message.User_Id
                   createdAt = message.Created_At |}
            )

        return
            insertedMessage
            |> Seq.tryHead
            |> Option.defaultValue message
}

let getUserMessages (userId: int64): Task<DbMessage array> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql = "SELECT * FROM message WHERE user_id = @userId"
            
        let! messages =
            conn.QueryAsync<DbMessage>(
                sql,
                {| userId = userId |}
            )
        return Array.ofSeq messages
    }

let deleteUserMessages (userId: int64): Task<int> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql = "DELETE FROM message WHERE user_id = @userId"
            
        let! messagesDeleted =
            conn.ExecuteAsync(
                sql,
                {| userId = userId |}
            )
        return messagesDeleted
    }

let cleanupOldMessages (howOld: TimeSpan): Task<int> =
    task {
        use conn = new NpgsqlConnection(connString)
        
        //language=postgresql
        let sql = "DELETE FROM message WHERE created_at < @thatOld"
        
        let! messagesDeleted =
            conn.ExecuteAsync(
                sql,
                {| thatOld = DateTime.UtcNow.Subtract howOld |}
            )
        
        return messagesDeleted
    }
