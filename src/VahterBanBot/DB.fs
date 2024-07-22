module VahterBanBot.DB

open System
open System.Threading.Tasks
open Microsoft.ML.Data
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
ON CONFLICT (banned_user_id) DO NOTHING;
            """

        let! _ = conn.ExecuteAsync(sql, banned)
        return banned
    }

let banUserByBot (banned: DbBanned) : Task =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
INSERT INTO banned_by_bot (message_id, message_text, banned_user_id, banned_at, banned_in_chat_id, banned_in_chat_username)
VALUES (@message_id, @message_text, @banned_user_id, @banned_at, @banned_in_chat_id, @banned_in_chat_username)
            """

        let! _ = conn.ExecuteAsync(sql, banned)
        return banned
    }

let unbanUserByBot (msg: DbMessage) : Task =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
DELETE FROM banned_by_bot WHERE message_id = @message_id and banned_in_chat_id = @chat_id
            """

        let! _ = conn.ExecuteAsync(sql, msg)
        return ()
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
    
let cleanupOldCallbacks (howOld: TimeSpan): Task<int> =
    task {
        use conn = new NpgsqlConnection(connString)
        
        //language=postgresql
        let sql = "DELETE FROM callback WHERE created_at < @thatOld"
        return! conn.ExecuteAsync(sql, {| thatOld = DateTime.UtcNow.Subtract howOld |})
    }

let getVahterStats(banInterval: TimeSpan option): Task<VahterStats> =
    task {
        use conn = new NpgsqlConnection(connString)
        
        //language=postgresql
        let sql =
            """
(SELECT vahter.username                                                      AS vahter
      , COUNT(*)                                                             AS killCountTotal
      , COUNT(*) FILTER (WHERE b.banned_at > NOW() - @banInterval::INTERVAL) AS killCountInterval
 FROM banned b
          JOIN "user" vahter ON vahter.id = b.banned_by
 GROUP BY b.banned_by, vahter.username
 UNION
 SELECT 'bot'                                                          AS vahter,
        COUNT(*)                                                       AS killCountTotal,
        COUNT(*) FILTER (WHERE bbb.banned_at > NOW() - NULL::INTERVAL) AS killCountInterval
 FROM (SELECT banned_user_id, MIN(banned_at) AS banned_at
       FROM banned_by_bot
       GROUP BY banned_user_id) bbb)
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

type SpamOrHam =
    { [<LoadColumn(0)>]
      text: string
      [<LoadColumn(1)>]
      spam: bool }

let mlData(criticalDate: DateTime) : Task<SpamOrHam array> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
WITH really_banned AS (SELECT *
                       FROM banned b
                       -- known false positive spam messages
                       WHERE NOT EXISTS(SELECT 1 FROM false_positive_users fpu WHERE fpu.user_id = b.banned_user_id)
                         AND NOT EXISTS(SELECT 1
                                        FROM false_positive_messages fpm
                                        WHERE fpm.text = b.message_text)
                         AND b.message_text IS NOT NULL
                         AND b.banned_at <= @criticalDate),
     spam_or_ham AS (SELECT DISTINCT COALESCE(m.text, re_id.message_text) AS text,
                                     CASE
                                         -- known false negative spam messages
                                         WHEN EXISTS(SELECT 1
                                                     FROM false_negative_messages fnm
                                                     WHERE fnm.chat_id = m.chat_id
                                                       AND fnm.message_id = m.message_id)
                                             THEN TRUE
                                         WHEN re_id.banned_user_id IS NULL AND re_text.banned_user_id IS NULL
                                             THEN FALSE
                                         ELSE TRUE
                                         END                              AS spam
                     FROM (SELECT * FROM message WHERE text IS NOT NULL AND created_at <= @criticalDate) m
                              FULL OUTER JOIN really_banned re_id
                                              ON m.message_id = re_id.message_id AND m.chat_id = re_id.banned_in_chat_id
                              LEFT JOIN really_banned re_text ON m.text = re_text.message_text)
SELECT *
FROM spam_or_ham
ORDER BY RANDOM();
"""

        let! data = conn.QueryAsync<SpamOrHam>(sql, {| criticalDate = criticalDate |})
        return Array.ofSeq data
    }

let unbanUser (userId: int64): Task =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
DELETE FROM banned
WHERE banned_user_id = @userId
            """
        
        let! _ = conn.ExecuteAsync(sql, {| userId = userId |})
        return ()
    }

let markMessageAsFalsePositive (message: DbMessage): Task =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
INSERT INTO false_positive_messages (text) 
VALUES (@text)
ON CONFLICT DO NOTHING;
            """

        return! conn.ExecuteAsync(sql, message)
    }

let newCallback (data: CallbackMessage): Task<DbCallback> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
INSERT INTO callback (data)
VALUES (@data::JSONB)
RETURNING *;
            """

        return! conn.QuerySingleAsync<DbCallback>(sql, {| data = data |})
    }

let getCallback (id: Guid): Task<DbCallback option> = 
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql = "SELECT * FROM callback WHERE id = @id"

        let! result = conn.QueryAsync<DbCallback>(sql, {| id = id |})
        return Seq.tryHead result
    }

let deleteCallback (id: Guid): Task = 
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql = "DELETE FROM callback WHERE id = @id"

        let! _ = conn.QueryAsync<DbCallback>(sql, {| id = id |})
        return ()
    }
