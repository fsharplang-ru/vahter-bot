module VahterBanBot.DB

open System
open System.Threading.Tasks
open Microsoft.ML.Data
open Npgsql
open VahterBanBot.Types
open Dapper
open VahterBanBot.Utils

let private connString = getEnv "DATABASE_URL"
let private truncateTextLimit = getEnvOr "DATABASE_TRUNCATE_TEXT_LIMIT" "3000" |> int

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
        COUNT(*) FILTER (WHERE bbb.banned_at > NOW() - @banInterval::INTERVAL) AS killCountInterval
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
        let sql = """SELECT * FROM "user" WHERE id = @userId"""
        let! users = conn.QueryAsync<DbUser>(sql, {| userId = userId |})
        return users |> Seq.tryHead
    }

[<CLIMutable>]
type SpamOrHamDb =
    { text: string
      spam: bool
      less_than_n_messages: bool
      custom_emoji_count: int
      created_at: DateTime }

let mlData (criticalMsgCount: int) (criticalDate: DateTime) : Task<SpamOrHamDb array> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
WITH custom_emojis AS (SELECT message.id, COUNT(*) FILTER (WHERE entities ->> 'type' = 'custom_emoji') AS custom_emoji_count
                       FROM message,
                            LATERAL JSONB_ARRAY_ELEMENTS(raw_message -> 'entities') AS entities
                       GROUP BY message.id),
     less_than_n_messages AS (SELECT u.id, COUNT(DISTINCT m.text) < @criticalMsgCount AS less_than_n_messages
                              FROM "user" u
                                       LEFT JOIN message m ON u.id = m.user_id
                              GROUP BY u.id),
     really_banned AS (SELECT *
                       FROM banned b
                       -- known false positive spam messages
                       WHERE NOT EXISTS(SELECT 1 FROM false_positive_users fpu WHERE fpu.user_id = b.banned_user_id)
                         AND NOT EXISTS(SELECT 1
                                        FROM false_positive_messages fpm
                                        WHERE fpm.text_hash = md5(b.message_text)::uuid
                                          AND fpm.text = b.message_text)
                         AND b.message_text IS NOT NULL
                         AND b.banned_at >= @criticalDate),
     spam_or_ham AS (SELECT x.text,
                            x.spam,
                            x.less_than_n_messages,
                            x.custom_emoji_count,
                            MAX(x.created_at) AS created_at
                     FROM (SELECT DISTINCT COALESCE(m.text, re_id.message_text)                       AS text,
                                           CASE
                                               -- known false negative spam messages
                                               WHEN (EXISTS(SELECT 1
                                                            FROM false_negative_messages fnm
                                                            WHERE fnm.chat_id = m.chat_id
                                                              AND fnm.message_id = m.message_id)
                                                   -- known banned spam messages by bot, and not marked as false positive
                                                   OR EXISTS(SELECT 1
                                                             FROM banned_by_bot bbb
                                                             WHERE bbb.banned_in_chat_id = m.chat_id
                                                               AND bbb.message_id = m.message_id))
                                                   THEN TRUE
                                               WHEN re_id.banned_user_id IS NULL AND re_text.banned_user_id IS NULL
                                                   THEN FALSE
                                               ELSE TRUE
                                               END                                                    AS spam,
                                           COALESCE(l.less_than_n_messages, TRUE)                     AS less_than_n_messages,
                                           COALESCE(ce.custom_emoji_count, 0)                         AS custom_emoji_count,
                                           COALESCE(re_id.banned_at, re_text.banned_at, m.created_at) AS created_at
                           FROM (SELECT *
                                 FROM message
                                 WHERE text IS NOT NULL
                                   AND created_at >= @criticalDate) m
                                    FULL OUTER JOIN really_banned re_id
                                                    ON m.message_id = re_id.message_id AND m.chat_id = re_id.banned_in_chat_id
                                    LEFT JOIN really_banned re_text ON m.text = re_text.message_text
                                    LEFT JOIN custom_emojis ce ON m.id = ce.id
                                    LEFT JOIN less_than_n_messages l ON m.user_id = l.id) x
                     GROUP BY text, spam, less_than_n_messages, custom_emoji_count)
SELECT *
FROM spam_or_ham
ORDER BY created_at;
"""

        let! data = conn.QueryAsync<SpamOrHamDb>(sql, {| criticalDate = criticalDate; criticalMsgCount = criticalMsgCount |})
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
        
        // we need to truncate the text to work around the fact that
        // text is being indexed and btree index has a limit of 2704 bytes
        // https://dba.stackexchange.com/questions/25138/index-max-row-size-error/25139
        let message = { message with text = message.text.Substring(0, Math.Min(truncateTextLimit, message.text.Length)) }

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

let countUniqueUserMsg (userId: int64): Task<int> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql = "SELECT COUNT(DISTINCT text) FROM message WHERE user_id = @userId"

        let! result = conn.QuerySingleAsync<int>(sql, {| userId = userId |})
        return result
    }

let isBannedByVahter (vahterId: int64) (userId: int64): Task<bool> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql = "SELECT EXISTS(SELECT 1 FROM banned WHERE banned_user_id = @userId AND banned_by = @vahterId)"

        let! result = conn.QuerySingleAsync<bool>(sql, {| userId = userId; vahterId = vahterId |})
        return result
    }

let getUserStatsByLastNMessages (n: int) (userId: int64): Task<UserStats> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
WITH stats AS (SELECT m.message_id,
                      m.chat_id,
                      b.id IS NOT NULL        AS banned,
                      bbb.id IS NOT NULL      AS banned_by_bot,
                      fnm.chat_id IS NOT NULL AS false_neg,
                      fpm.text IS NOT NULL    AS false_pos
               FROM message m
                        LEFT JOIN banned b ON m.message_id = b.message_id AND m.chat_id = b.banned_in_chat_id
                        LEFT JOIN public.banned_by_bot bbb
                                  ON m.message_id = bbb.message_id AND m.chat_id = bbb.banned_in_chat_id
                        LEFT JOIN public.false_negative_messages fnm
                                  ON m.message_id = fnm.message_id AND m.chat_id = fnm.chat_id
                        LEFT JOIN false_positive_messages fpm ON fpm.text_hash = md5(m.text)::uuid
                                                             AND fpm.text = m.text
               WHERE m.user_id = @userId
               ORDER BY m.created_at DESC
               LIMIT @n),
     stats_count AS (SELECT message_id,
                            chat_id,
                            CASE WHEN false_pos THEN FALSE ELSE banned OR banned_by_bot OR false_neg END AS spam
                     FROM stats)
SELECT COUNT(*) FILTER (WHERE NOT spam) AS good,
       COUNT(*) FILTER (WHERE spam)     AS bad
FROM stats_count;
            """

        let! result = conn.QuerySingleAsync<UserStats>(sql, {| userId = userId; n = n |})
        return result
    }
