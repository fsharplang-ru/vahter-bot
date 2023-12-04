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
    SET username   = COALESCE("user".username, EXCLUDED.username),
        ban_reason = COALESCE("user".ban_reason, EXCLUDED.ban_reason),
        banned_at  = COALESCE("user".banned_at, EXCLUDED.banned_at),
        banned_by  = COALESCE("user".banned_by, EXCLUDED.banned_by),
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
            
        let! messages = conn.QueryAsync<DbMessage>(sql, {| userId = userId |})
        return Array.ofSeq messages
    }

let deleteMsgs (msg: DbMessage[]): Task<int> =
    task {
        let msgIds = msg |> Array.map (fun m -> m.Message_Id)
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql = "DELETE FROM message WHERE message_id = ANY(@msgIds)"
        return! conn.ExecuteAsync(sql, {| msgIds = msgIds |})
    }

let cleanupOldMessages (howOld: TimeSpan): Task<int> =
    task {
        use conn = new NpgsqlConnection(connString)
        
        //language=postgresql
        let sql = "DELETE FROM message WHERE created_at < @thatOld"
        return! conn.ExecuteAsync(sql, {| thatOld = DateTime.UtcNow.Subtract howOld |})
    }

let getVahterStats(banInterval: TimeSpan option): Task<VahterStats> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
SELECT vahter.username                                                      AS vahter
     , COUNT(*)                                                             AS killCountTotal
     , COUNT(*) FILTER (WHERE u.banned_at > NOW() - @banInterval::INTERVAL) AS killCountInterval
FROM "user" u
         JOIN "user" vahter ON vahter.id = u.banned_by
WHERE u.banned_by IS NOT NULL
GROUP BY u.banned_by, vahter.username
ORDER BY killCountTotal DESC
            """

        let! stats = conn.QueryAsync<VahterStat>(sql, {| banInterval = banInterval |})
        return { interval = banInterval; stats = Array.ofSeq stats }
    }

let getUserById (userId: int64): Task<DbUser option> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql = "SELECT * FROM \"user\" WHERE id = @userId"
        let! users = conn.QueryAsync<DbUser>(sql, {| userId = userId |})
        return users |> Seq.tryHead
    }