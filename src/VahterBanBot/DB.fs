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
INSERT INTO "user" (id, username, created_at, updated_at)
VALUES (@id, @username, @created_at, @updated_at)
ON CONFLICT (id) DO UPDATE
    SET username   = COALESCE("user".username, EXCLUDED.username),
        updated_at = GREATEST(EXCLUDED.updated_at, "user".updated_at)
RETURNING *;
"""

        let! insertedUser = conn.QueryAsync<DbUser>(sql, user)

        return insertedUser |> Seq.head
    }

let insertMessage (message: DbMessage): Task<DbMessage> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
INSERT INTO message (chat_id, message_id, user_id, text, raw_message, created_at)
VALUES (@chat_id, @message_id, @user_id, @text, @raw_message::JSONB, @created_at)
ON CONFLICT (chat_id, message_id) DO NOTHING RETURNING *;
            """

        let! insertedMessage = conn.QueryAsync<DbMessage>(sql, message)

        return
            insertedMessage
            |> Seq.tryHead
            |> Option.defaultValue message
}

let banUser (banned: DbBanned): Task =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
INSERT INTO banned (message_id, message_text, banned_user_id, banned_at, banned_in_chat_id, banned_in_chat_username, banned_by)
VALUES (@message_id, @message_text, @banned_user_id, @banned_at, @banned_in_chat_id, @banned_in_chat_username, @banned_by)
            """
        
        let! _ = conn.ExecuteAsync(sql, banned)
        return banned
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
        let msgIds = msg |> Array.map (_.message_id)
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
     , COUNT(*) FILTER (WHERE b.banned_at > NOW() - @banInterval::INTERVAL) AS killCountInterval
FROM banned b
         JOIN "user" vahter ON vahter.id = b.banned_by
GROUP BY b.banned_by, vahter.username
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
