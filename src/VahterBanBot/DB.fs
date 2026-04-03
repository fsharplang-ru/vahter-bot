module VahterBanBot.DB

open System
open System.Data
open System.IO
open System.Threading.Tasks
open Microsoft.ML.Data
open Npgsql
open NpgsqlTypes
open VahterBanBot.Types
open Dapper
open VahterBanBot.Utils

let private connString = getEnv "DATABASE_URL"

// ---------------------------------------------------------------------------
// Generic serialize / deserialize via FSharp.SystemTextJson
// ---------------------------------------------------------------------------

open System.Text.Json

/// Alias for the shared event JSON options defined in Types.
let fsharpJsonOpts = eventJsonOpts

/// Deserializes data JSON → DU event.
let deserializeEvent<'Event> (raw: RawEvent) : 'Event =
    JsonSerializer.Deserialize<'Event>(raw.data, options = fsharpJsonOpts)

/// SRTP helper — folds raw events into aggregate state using Fold/Zero from the state type.
let inline foldEvents<'Event, 'State
    when 'State : (static member Zero : 'State)
    and  'State : (static member Fold : 'State * 'Event -> 'State)>
    (rawEvents: RawEvent list) : 'State =
    let fold s e = 'State.Fold(s, e)
    rawEvents |> List.map deserializeEvent<'Event> |> List.fold fold 'State.Zero

// ---------------------------------------------------------------------------
// Event store primitives
// ---------------------------------------------------------------------------

/// Reads all events for a stream; returns (events, currentVersion) where
/// currentVersion = 0 means the stream does not exist yet.
let readStream (streamId: string) : Task<RawEvent list * int> =
    task {
        use conn = new NpgsqlConnection(connString)
        //language=postgresql
        let sql =
            """
SELECT stream_id, stream_version, event_type, data::TEXT AS data, created_at
FROM event
WHERE stream_id = @streamId
ORDER BY stream_version
            """
        let! rows = conn.QueryAsync<RawEvent>(sql, {| streamId = streamId |})
        let events = List.ofSeq rows
        let version = events |> List.tryLast |> Option.map (fun e -> e.stream_version) |> Option.defaultValue 0
        return events, version
    }

/// Tries to append events to a stream at the expected version.
/// Serializes DU events to JSON internally — callers pass typed events, not RawEvents.
/// Returns Ok () on success, Error ConcurrencyConflict if stream_version already exists.
let tryAppend<'Event> (streamId: string) (expectedVersion: int) (events: 'Event list) : Task<Result<unit, ConcurrencyConflict>> =
    task {
        if events.IsEmpty then return Ok ()
        else
        use conn = new NpgsqlConnection(connString)
        do! conn.OpenAsync()
        use! tx = conn.BeginTransactionAsync()
        //language=postgresql
        let sql =
            """
INSERT INTO event(stream_id, stream_version, data)
VALUES (@stream_id, @stream_version, @data::JSONB)
ON CONFLICT (stream_id, stream_version) DO NOTHING
RETURNING id
            """
        let mutable insertedCount = 0
        for (i, e) in events |> List.indexed do
            let data = JsonSerializer.Serialize<'Event>(e, fsharpJsonOpts)
            let! rows = conn.QueryAsync<int64>(sql, {| stream_id = streamId; stream_version = expectedVersion + i + 1; data = data |})
            insertedCount <- insertedCount + Seq.length rows
        if insertedCount < events.Length then
            do! tx.RollbackAsync()
            return Error ConcurrencyConflict
        else
            do! tx.CommitAsync()
            return Ok ()
    }

/// Optimistic-concurrency transact loop.
/// Reads stream → folds events into state → calls decider → appends.
/// Retries on ConcurrencyConflict. Never recurses — uses a while loop (task{} is hot/eager).
/// Returns the new events and the final aggregate state.
let transact
    (fold:        'State -> 'Event -> 'State)
    (initial:     'State)
    (decider:     'State -> 'Event list)
    (streamId:    string)
    : Task<'Event list * 'State> =
    task {
        let mutable result = ValueNone
        while result.IsNone do
            let! (rawEvents, version) = readStream streamId
            let state     = rawEvents |> List.map deserializeEvent<'Event> |> List.fold fold initial
            let newEvents = decider state
            if newEvents.IsEmpty then
                result <- ValueSome ([], state)
            else
                match! tryAppend streamId version newEvents with
                | Ok _                    ->
                    let finalState = newEvents |> List.fold fold state
                    result <- ValueSome (newEvents, finalState)
                | Error ConcurrencyConflict -> ()   // re-read, re-decide
        return result.Value
    }

// ---------------------------------------------------------------------------
// Transact-based write helpers (replace legacy insert/update functions)
// ---------------------------------------------------------------------------

/// SRTP wrapper — resolves Fold/Zero from the state type at compile time.
let inline appendEvent (streamId: string) (decider: 'State -> 'Event list) : Task<'Event list * 'State>
    when 'State : (static member Zero : 'State)
    and  'State : (static member Fold : 'State * 'Event -> 'State) =
    let fold s e = 'State.Fold(s, e)
    transact fold 'State.Zero decider streamId

/// Records a UsernameChanged event only when the username actually differs.
let recordUsernameChanged (userId: int64) (username: string option) : Task<unit> =
    task {
        let! _ = appendEvent $"user:{userId}" (fun state ->
            if state.Username = username then []
            else [ UsernameChanged {| userId = userId; username = username |} ])
        return ()
    }

/// Records a UsernameChanged (if needed) with atomic reaction increment.
let recordUserReaction (userId: int64) (username: string option) (reactionIncrement: int) : Task<User> =
    task {
        let! (_, state) = appendEvent $"user:{userId}" (fun state ->
            let usernameEvt =
                if state.Username = username then []
                else [ UsernameChanged {| userId = userId; username = username |} ]
            usernameEvt @ [ UserReactionRecorded {| userId = userId; delta = reactionIncrement |} ])
        return state
    }

/// Records a UserBanned event (replaces banUser for writes).
let recordUserBanned (userId: int64) (bannedBy: BannedBy) : Task<unit> =
    task {
        let! _ = appendEvent $"user:{userId}" (fun (state: User) ->
            if state.IsBanned then []   // idempotent — already banned
            else [ UserBanned {| userId = userId; bannedBy = bannedBy |} ])
        return ()
    }

/// Records a UserUnbanned event (replaces unbanUser for writes).
let recordUserUnbanned (userId: int64) (unbannedBy: int64) : Task<unit> =
    task {
        let! _ = appendEvent $"user:{userId}" (fun (state: User) ->
            if not state.IsBanned then []
            else [ UserUnbanned {| userId = userId; unbannedBy = unbannedBy |} ])
        return ()
    }

/// Records a MessageReceived event (replaces insertMessage for writes).
let recordMessageReceived (chatId: int64) (messageId: int) (userId: int64) (text: string option) (rawMessage: string) : Task<unit> =
    task {
        let! _ = appendEvent $"message:{chatId}:{messageId}" (fun state ->
            if state.Received then []
            else [ MessageReceived {| chatId = chatId; messageId = messageId; userId = userId; text = text; rawMessage = rawMessage |} ])
        return ()
    }

/// Records a MessageEdited event (appends to existing message stream).
let recordMessageEdited (chatId: int64) (messageId: int) (userId: int64) (text: string option) (rawMessage: string) : Task<unit> =
    task {
        let! _ = appendEvent $"message:{chatId}:{messageId}" (fun (_: Message) ->
            [ MessageEdited {| chatId = chatId; messageId = messageId; userId = userId; text = text; rawMessage = rawMessage |} ])
        return ()
    }

/// Records a MessageMarkedHam event. Latest Spam/Ham decision wins.
let recordMessageMarkedHam (chatId: int64) (messageId: int) (text: string) (markedBy: int64 option) : Task<unit> =
    task {
        let! _ = appendEvent $"message:{chatId}:{messageId}" (fun state ->
            if state.Classification = SpamClassification.Ham then []   // already ham
            else [ MessageMarkedHam {| chatId = chatId; messageId = messageId; text = text; markedBy = markedBy |} ])
        return ()
    }

/// Records a MessageMarkedSpam event. Latest Spam/Ham decision wins.
let recordMessageMarkedSpam (chatId: int64) (messageId: int) (markedBy: int64 option) : Task<unit> =
    task {
        let! _ = appendEvent $"message:{chatId}:{messageId}" (fun state ->
            if state.Classification = SpamClassification.Spam then []  // already spam
            else [ MessageMarkedSpam {| chatId = chatId; messageId = messageId; markedBy = markedBy |} ])
        return ()
    }

/// Records a VahterActed event. Allows multiple actions on the same message
/// (e.g., ban then mark not-spam). Returns true if this was the first action
/// (used by callers for deduplication of ban processing).
let recordVahterAction
    (vahterId: int64) (actionType: VahterAction) (targetUserId: int64)
    (chatId: int64) (messageId: int) : Task<bool> =
    task {
        let! (_, state) = appendEvent $"moderation:{chatId}:{messageId}" (fun (_: Moderation) ->
            [ VahterActed {| vahterId = vahterId; actionType = actionType; targetUserId = targetUserId; chatId = chatId; messageId = messageId |} ])
        return state.VahterActedCount <= 1
    }

/// Records a BotAutoDeleted event. NOT idempotent — each call adds an event
/// (each auto-deletion attempt counts toward the user's social score).
let recordBotAutoDeleted (chatId: int64) (messageId: int) (userId: int64) (reason: AutoDeleteReason) : Task<unit> =
    task {
        let! _ = appendEvent $"moderation:{chatId}:{messageId}" (fun (_: Moderation) ->
            [ BotAutoDeleted {| chatId = chatId; messageId = messageId; userId = userId; reason = reason |} ])
        return ()
    }

/// Records an MlScoredMessage event for observability and determinism testing.
let recordMlScoredMessage
    (chatId: int64) (messageId: int) (score: float) (isSpam: bool) : Task<unit> =
    task {
        let! _ = appendEvent $"detection:{chatId}:{messageId}" (fun (_: Detection) ->
            [ MlScoredMessage {| chatId = chatId; messageId = messageId; score = score; isSpam = isSpam |} ])
        return ()
    }

/// Records an LlmClassified event (replaces insertLlmTriage for writes).
// TODO: once all historical LlmClassified events have been backfilled with modelName and
// promptHash, the option wrappers can be removed from those fields.
let recordLlmClassified
    (chatId: int64) (messageId: int) (verdict: string)
    (promptTokens: int) (completionTokens: int) (latencyMs: int)
    (modelName: string option) (promptHash: string option) : Task<unit> =
    task {
        let! _ = appendEvent $"detection:{chatId}:{messageId}" (fun (_: Detection) ->
            [ LlmClassified {| chatId = chatId; messageId = messageId; verdict = verdict
                               promptTokens = promptTokens; completionTokens = completionTokens; latencyMs = latencyMs
                               modelName = modelName; promptHash = promptHash |} ])
        return ()
    }

let upsertUser (userId: int64) (username: string option): Task<User> =
    task {
        let! (_, state) = appendEvent $"user:{userId}" (fun (state: User) ->
            if state.Username = username then []
            else [ UsernameChanged {| userId = userId; username = username |} ])
        return { state with Id = userId }
    }

let upsertUserAndIncrementReactions (userId: int64) (username: string option) (reactionIncrement: int): Task<User> =
    task {
        let! state = recordUserReaction userId username reactionIncrement
        return { state with Id = userId }
    }

let insertMessage (msg: TgMessage): Task =
    task {
        do! recordUsernameChanged msg.SenderId (Option.ofObj msg.SenderUsername)
        do! recordMessageReceived msg.ChatId msg.MessageId msg.SenderId
                (Option.ofObj msg.Text)
                msg.RawJson
    }

let editMessage (msg: TgMessage): Task =
    task {
        do! recordUsernameChanged msg.SenderId (Option.ofObj msg.SenderUsername)
        do! recordMessageEdited msg.ChatId msg.MessageId msg.SenderId
                (Option.ofObj msg.Text)
                msg.RawJson
    }

/// Lightweight DTO for getUserMessages (used for message deletion).
[<CLIMutable>]
type UserMessage =
    { chat_id: int64
      message_id: int }

let getUserMessages (userId: int64): Task<UserMessage array> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
SELECT
    (data->>'chatId')::BIGINT    AS chat_id,
    (data->>'messageId')::INT    AS message_id
FROM event
WHERE event_type = 'MessageReceived'
  AND (data->>'userId')::BIGINT = @userId
            """

        let! messages = conn.QueryAsync<UserMessage>(sql, {| userId = userId |})
        return Array.ofSeq messages
    }

// ---------------------------------------------------------------------------
// Callback event-sourced functions
// ---------------------------------------------------------------------------

/// Creates a callback by appending CallbackCreated + CallbackMessagePosted events.
let recordCallbackCreated (callbackId: Guid) (data: CallbackMessage) (targetUserId: int64) (channelId: int64) : Task<unit> =
    task {
        let serializedData = serializeCallbackData data
        let! _ = appendEvent $"callback:{callbackId}" (fun (_: Callback) ->
            [ CallbackCreated {| data = serializedData; targetUserId = targetUserId; actionChannelId = channelId |} ])
        return ()
    }

/// Records the action message ID after posting to channel.
let recordCallbackMessagePosted (callbackId: Guid) (messageId: int) : Task<unit> =
    task {
        let! _ = appendEvent $"callback:{callbackId}" (fun (state: Callback) ->
            if state.IsTerminal || state.ActionMessageId.IsSome then []
            else [ CallbackMessagePosted {| actionMessageId = messageId |} ])
        return ()
    }

/// Atomically resolves a callback (vahter clicked a button).
/// Returns Some aggregate state if resolved, None if already terminal.
let resolveCallback (callbackId: Guid) : Task<Callback option> =
    task {
        let! (events, state) = appendEvent $"callback:{callbackId}" (fun (state: Callback) ->
            if state.IsTerminal || state.Data.IsNone then []
            else [ CallbackResolved ])
        return if events.IsEmpty then None else Some state
    }

/// Expires a callback (cleanup/orphaned — no vahter action taken).
let expireCallback (callbackId: Guid) : Task<unit> =
    task {
        let! _ = appendEvent $"callback:{callbackId}" (fun (state: Callback) ->
            if state.IsTerminal then []
            else [ CallbackExpired ])
        return ()
    }

/// Gets all active (non-terminal) callbacks for a user.
let getActiveCallbacksByUserId (userId: int64) : Task<ActiveCallbackInfo array> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
SELECT
    REPLACE(e.stream_id, 'callback:', '')::UUID AS id,
    (e.data->>'actionChannelId')::BIGINT AS action_channel_id,
    (SELECT (e3.data->>'actionMessageId')::INT
     FROM event e3
     WHERE e3.stream_id = e.stream_id
       AND e3.event_type = 'CallbackMessagePosted'
     LIMIT 1) AS action_message_id
FROM event e
WHERE e.event_type = 'CallbackCreated'
  AND (e.data->>'targetUserId')::BIGINT = @userId
  AND NOT EXISTS (
      SELECT 1 FROM event e2
      WHERE e2.stream_id = e.stream_id
        AND e2.event_type IN ('CallbackResolved', 'CallbackExpired')
  )
            """

        let! result = conn.QueryAsync<ActiveCallbackInfo>(sql, {| userId = userId |})
        return Array.ofSeq result
    }

/// Gets active callbacks without a posted message (failed posts), older than the given age.
let getFailedCallbackPosts (age: TimeSpan) : Task<Guid array> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
SELECT REPLACE(e.stream_id, 'callback:', '')::UUID
FROM event e
WHERE e.event_type = 'CallbackCreated'
  AND e.created_at < @cutoff
  AND NOT EXISTS (
      SELECT 1 FROM event e2
      WHERE e2.stream_id = e.stream_id
        AND e2.event_type = 'CallbackMessagePosted'
  )
  AND NOT EXISTS (
      SELECT 1 FROM event e3
      WHERE e3.stream_id = e.stream_id
        AND e3.event_type IN ('CallbackResolved', 'CallbackExpired')
  )
            """

        let! result = conn.QueryAsync<Guid>(sql, {| cutoff = DateTime.UtcNow.Subtract age |})
        return Array.ofSeq result
    }

/// Gets active callbacks in a specific channel, older than the given age.
let getOldCallbacksInChannel (age: TimeSpan) (channelId: int64) : Task<ActiveCallbackInfo array> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
SELECT
    REPLACE(e.stream_id, 'callback:', '')::UUID AS id,
    (e.data->>'actionChannelId')::BIGINT AS action_channel_id,
    (SELECT (e3.data->>'actionMessageId')::INT
     FROM event e3
     WHERE e3.stream_id = e.stream_id
       AND e3.event_type = 'CallbackMessagePosted'
     LIMIT 1) AS action_message_id
FROM event e
WHERE e.event_type = 'CallbackCreated'
  AND (e.data->>'actionChannelId')::BIGINT = @channelId
  AND e.created_at < @cutoff
  AND NOT EXISTS (
      SELECT 1 FROM event e2
      WHERE e2.stream_id = e.stream_id
        AND e2.event_type IN ('CallbackResolved', 'CallbackExpired')
  )
            """

        let! result = conn.QueryAsync<ActiveCallbackInfo>(sql, {| channelId = channelId; cutoff = DateTime.UtcNow.Subtract age |})
        return Array.ofSeq result
    }

/// Expires all callbacks sharing the same action message ID (sibling buttons).
let expireCallbacksByMessageId (actionMessageId: int) : Task<unit> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
SELECT REPLACE(e.stream_id, 'callback:', '')::UUID
FROM event e
WHERE e.event_type = 'CallbackMessagePosted'
  AND (e.data->>'actionMessageId')::INT = @msgId
  AND NOT EXISTS (
      SELECT 1 FROM event e2
      WHERE e2.stream_id = e.stream_id
        AND e2.event_type IN ('CallbackResolved', 'CallbackExpired')
  )
            """

        let! callbackIds = conn.QueryAsync<Guid>(sql, {| msgId = actionMessageId |})
        for callbackId in callbackIds do
            do! expireCallback callbackId
    }

/// Expires orphaned callbacks older than the given age.
/// Uses per-callback appendEvent calls for proper optimistic concurrency.
let expireOrphanedCallbacks (howOld: TimeSpan) : Task<int> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
SELECT REPLACE(stream_id, 'callback:', '')::UUID
FROM event
WHERE event_type = 'CallbackCreated'
  AND created_at < @cutoff
  AND NOT EXISTS (
      SELECT 1 FROM event e2
      WHERE e2.stream_id = event.stream_id
        AND e2.event_type IN ('CallbackResolved', 'CallbackExpired')
  )
            """

        let! orphanedIds = conn.QueryAsync<Guid>(sql, {| cutoff = DateTime.UtcNow.Subtract howOld |})
        let ids = Array.ofSeq orphanedIds
        for callbackId in ids do
            do! expireCallback callbackId
        return ids.Length
    }

let getVahterStats(banInterval: TimeSpan option): Task<VahterStats> =
    task {
        use conn = new NpgsqlConnection(connString)
        
        //language=postgresql
        let sql =
            """
SELECT * FROM (
    SELECT vahter.username                                                      AS "Vahter"
          , COUNT(*)                                                             AS "KillCountTotal"
          , COUNT(*) FILTER (WHERE b.banned_at > NOW() - @banInterval::INTERVAL) AS "KillCountInterval"
     FROM banned b
              JOIN "user" vahter ON vahter.id = b.banned_by
     GROUP BY b.banned_by, vahter.username
     UNION
     SELECT 'bot'                                                          AS "Vahter",
            COUNT(*)                                                       AS "KillCountTotal",
            COUNT(*) FILTER (WHERE bbb.banned_at > NOW() - @banInterval::INTERVAL) AS "KillCountInterval"
     FROM (SELECT banned_user_id, MIN(banned_at) AS banned_at
           FROM banned_by_bot
           GROUP BY banned_user_id) bbb
) stats
ORDER BY "KillCountTotal" DESC;
            """

        let! stats = conn.QueryAsync<VahterStat>(sql, {| banInterval = banInterval |})
        return { VahterStats.interval = banInterval; stats = Array.ofSeq stats }
    }

let getUserById (userId: int64): Task<User option> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
SELECT stream_id, stream_version, event_type, data::TEXT AS data, created_at
FROM event
WHERE stream_id = 'user:' || @userId
ORDER BY stream_version
            """
        let! rows = conn.QueryAsync<RawEvent>(sql, {| userId = userId |})
        let events = List.ofSeq rows
        if events.IsEmpty then return None
        else
            let state : User = foldEvents<UserEvent, User> events
            return Some { state with Id = userId }
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
WITH msg_events AS (
    SELECT (e.data->>'chatId')::BIGINT                                                AS chat_id,
           (e.data->>'messageId')::INT                                                AS message_id,
           e.data->>'text'                                                            AS text,
           (e.data->>'userId')::BIGINT                                                AS user_id,
           (SELECT COUNT(*) FROM jsonb_array_elements(e.data->'rawMessage'->'entities') ent
            WHERE ent->>'type' = 'custom_emoji')::INT                                 AS custom_emoji_count,
           e.created_at
    FROM event e
    WHERE e.event_type = 'MessageReceived'
      AND e.created_at >= @criticalDate
      AND e.data->>'text' IS NOT NULL
),
user_msg_counts AS (
    SELECT (data->>'userId')::BIGINT AS user_id,
           COUNT(DISTINCT data->>'text') < @criticalMsgCount AS less_than_n_messages
    FROM event
    WHERE event_type = 'MessageReceived'
    GROUP BY 1
),
spam_or_ham AS (
    SELECT m.text,
           m.user_id,
           EXISTS(
               SELECT 1 FROM event e2
               WHERE e2.stream_id = 'moderation:' || m.chat_id || ':' || m.message_id
                 AND e2.event_type IN ('BotAutoDeleted', 'VahterActed')
                 AND (e2.data->>'actionType' IS NULL
                      OR e2.data->>'actionType' IN ('PotentialKill', 'ManualBan'))
           ) OR EXISTS(
               SELECT 1 FROM event e3
               WHERE e3.event_type = 'MessageMarkedSpam'
                 AND e3.stream_id = 'message:' || m.chat_id || ':' || m.message_id
           )                                                                          AS spam,
           COALESCE(u.less_than_n_messages, TRUE)                                     AS less_than_n_messages,
           m.custom_emoji_count,
           m.created_at
    FROM msg_events m
    LEFT JOIN user_msg_counts u ON u.user_id = m.user_id
    WHERE NOT EXISTS(
        SELECT 1 FROM event e_fp
        WHERE e_fp.event_type = 'MessageMarkedHam'
          AND e_fp.data->>'text' = m.text
    )
)
SELECT text,
       spam,
       less_than_n_messages,
       custom_emoji_count,
       MAX(created_at) AS created_at
FROM spam_or_ham
GROUP BY text, spam, less_than_n_messages, custom_emoji_count
ORDER BY MAX(created_at);
"""

        let! data = conn.QueryAsync<SpamOrHamDb>(sql, {| criticalDate = criticalDate; criticalMsgCount = criticalMsgCount |})
        return Array.ofSeq data
    }

let unbanUser (userId: int64) (vahterId: int64): Task =
    recordUserUnbanned userId vahterId

let markMessageAsFalsePositive (chatId: int64) (messageId: int) (text: string): Task =
    recordMessageMarkedHam chatId messageId (if isNull text then "" else text) None

/// Marks a message as false negative (spam that was not auto-detected)
/// Used for soft spam marking - counts toward karma but doesn't ban
let markMessageAsFalseNegative (chatId: int64) (messageId: int): Task =
    recordMessageMarkedSpam chatId messageId None

let countUniqueUserMsg (userId: int64): Task<int> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
SELECT COUNT(DISTINCT data->>'text')::INT
FROM event
WHERE event_type = 'MessageReceived'
  AND (data->>'userId')::BIGINT = @userId
            """

        let! result = conn.QuerySingleAsync<int>(sql, {| userId = userId |})
        return result
    }

let isBannedByVahter (vahterId: int64) (userId: int64): Task<bool> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
SELECT EXISTS(
    SELECT 1 FROM event
    WHERE stream_id = 'user:' || @userId
      AND event_type = 'UserBanned'
      AND data->'bannedBy'->>'Case' = 'BannedByVahter'
      AND (data->'bannedBy'->>'vahterId')::BIGINT = @vahterId
)
            """

        let! result = conn.QuerySingleAsync<bool>(sql, {| userId = userId; vahterId = vahterId |})
        return result
    }

let getUserStatsByLastNMessages (n: int) (userId: int64): Task<UserStats> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
WITH user_messages AS (
    SELECT data->>'text'             AS text,
           (data->>'chatId')::BIGINT AS chat_id,
           (data->>'messageId')::INT AS message_id,
           created_at
    FROM event
    WHERE event_type = 'MessageReceived'
      AND (data->>'userId')::BIGINT = @userId
),
bot_deletions AS (
    SELECT (data->>'chatId')::BIGINT AS chat_id,
           (data->>'messageId')::INT AS message_id
    FROM event
    WHERE event_type = 'BotAutoDeleted'
      AND (data->>'userId')::BIGINT = @userId
),
expanded AS (
    SELECT m.text, m.chat_id, m.message_id,
           d.message_id IS NOT NULL AS banned_by_bot,
           EXISTS(
               SELECT 1 FROM event e2
               WHERE e2.stream_id = 'moderation:' || m.chat_id || ':' || m.message_id
                 AND e2.event_type = 'VahterActed'
                 AND e2.data->>'actionType' IN ('PotentialKill', 'ManualBan')
           ) AS banned_by_vahter,
           EXISTS(
               SELECT 1 FROM event e3
               WHERE e3.event_type = 'MessageMarkedHam'
                 AND e3.data->>'text' = m.text
           ) AS is_ham,
           EXISTS(
               SELECT 1 FROM event e4
               WHERE e4.stream_id = 'message:' || m.chat_id || ':' || m.message_id
                 AND e4.event_type = 'MessageMarkedSpam'
           ) AS is_spam
    FROM user_messages m
    LEFT JOIN bot_deletions d ON m.chat_id = d.chat_id AND m.message_id = d.message_id
    ORDER BY m.created_at DESC
    LIMIT @n
)
SELECT COUNT(*) FILTER (WHERE CASE WHEN is_ham THEN TRUE
                                    ELSE NOT (banned_by_bot OR banned_by_vahter OR is_spam)
                               END) AS good,
       COUNT(*) FILTER (WHERE CASE WHEN is_ham THEN FALSE
                                    ELSE (banned_by_bot OR banned_by_vahter OR is_spam)
                               END) AS bad
FROM expanded;
            """

        let! result = conn.QuerySingleAsync<UserStats>(sql, {| userId = userId; n = n |})
        return result
    }

/// Records a vahter action via event stream. Returns true if recorded (first actor), false if already handled.
let tryRecordVahterAction
    (vahterId: int64)
    (actionType: VahterAction)
    (targetUserId: int64)
    (chatId: int64)
    (msgId: int): Task<bool> =
    recordVahterAction vahterId actionType targetUserId chatId msgId

/// Gets vahter action stats from event table
let getVahterActionStats (interval: TimeSpan option): Task<VahterActionStats> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
SELECT (SELECT e2.data->>'username'
        FROM event e2
        WHERE e2.stream_id = 'user:' || va_stats.vahter_id
          AND e2.event_type = 'UsernameChanged'
        ORDER BY e2.id DESC LIMIT 1) AS "Vahter",
       va_stats."KillsTotal",
       va_stats."KillsInterval",
       va_stats."NotSpamTotal",
       va_stats."NotSpamInterval"
FROM (
    SELECT va.data->>'vahterId' AS vahter_id,
           COUNT(*) FILTER (WHERE va.data->>'actionType' IN ('PotentialKill', 'ManualBan')) AS "KillsTotal",
           COUNT(*) FILTER (WHERE va.data->>'actionType' IN ('PotentialKill', 'ManualBan')
                              AND va.created_at > NOW() - @interval::INTERVAL) AS "KillsInterval",
           COUNT(*) FILTER (WHERE va.data->>'actionType' IN ('PotentialNotSpam', 'DetectedNotSpam')) AS "NotSpamTotal",
           COUNT(*) FILTER (WHERE va.data->>'actionType' IN ('PotentialNotSpam', 'DetectedNotSpam')
                              AND va.created_at > NOW() - @interval::INTERVAL) AS "NotSpamInterval"
    FROM event va
    WHERE va.event_type = 'VahterActed'
    GROUP BY va.data->>'vahterId'
) va_stats
ORDER BY va_stats."KillsTotal" + va_stats."NotSpamTotal" DESC;
            """

        let! stats = conn.QueryAsync<VahterActionStat>(sql, {| interval = interval |})
        return { interval = interval; stats = Array.ofSeq stats }
    }

/// Saves a trained ML model to the database (singleton row, upsert).
/// Accepts a Stream to avoid buffering the entire model in memory as byte[].
let saveTrainedModel (modelStream: Stream): Task =
    task {
        use conn = new NpgsqlConnection(connString)
        do! conn.OpenAsync()

        //language=postgresql
        let sql =
            """
INSERT INTO ml_trained_model (id, model_data, created_at)
VALUES (1, @modelData, NOW())
ON CONFLICT (id) DO UPDATE
    SET model_data = EXCLUDED.model_data,
        created_at = EXCLUDED.created_at;
            """

        use cmd = new NpgsqlCommand(sql, conn)
        cmd.Parameters.Add(NpgsqlParameter("modelData", NpgsqlTypes.NpgsqlDbType.Bytea, Value = modelStream)) |> ignore
        let! _ = cmd.ExecuteNonQueryAsync()
        return ()
    }

/// Loads a trained ML model from the database as a Stream.
/// Uses SequentialAccess to avoid buffering the entire bytea in memory.
/// The returned stream is only valid while the caller-provided action runs.
/// Column order: created_at first, then model_data (SequentialAccess requires reading in order).
let withTrainedModel (action: Stream * DateTime -> Task<'a>) : Task<'a option> =
    task {
        use conn = new NpgsqlConnection(connString)
        do! conn.OpenAsync()

        //language=postgresql
        let sql = "SELECT created_at, model_data FROM ml_trained_model WHERE id = 1"

        use cmd = new NpgsqlCommand(sql, conn)
        use! reader = cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess)
        let! hasRow = reader.ReadAsync()
        if hasRow then
            // Must read in column order with SequentialAccess
            let createdAt = reader.GetDateTime(0)
            use stream = reader.GetStream(1)
            let! result = action (stream, createdAt)
            return Some result
        else
            return None
    }

/// Gets the created_at timestamp of the trained model (lightweight freshness check).
let getModelCreatedAt (): Task<DateTime option> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql = "SELECT created_at FROM ml_trained_model WHERE id = 1"

        let! result = conn.QueryAsync<DateTime>(sql)
        return Seq.tryHead result
    }

/// Tries to acquire a scheduled job with lease mechanism.
/// Job runs once per day at the scheduled time (UTC).
/// Returns true if acquired, false if job is already running or not due yet.
/// Uses atomic UPDATE to ensure only one pod can acquire the job.
let tryAcquireScheduledJob (jobName: string) (scheduledTime: TimeSpan) (podId: string): Task<bool> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        // Job should run if:
        // 1. Current time >= today's scheduled time
        // 2. Haven't completed today (last_completed_at < today's scheduled time or NULL)
        // 3. Not locked or lock expired
        let sql =
            """
UPDATE scheduled_job
SET locked_until = NOW() + INTERVAL '1 hour',
    locked_by = @podId
WHERE job_name = @jobName
  AND NOW() >= (CURRENT_DATE + @scheduledTime)
  AND (last_completed_at IS NULL OR last_completed_at < (CURRENT_DATE + @scheduledTime))
  AND (locked_until IS NULL OR locked_until < NOW())
RETURNING job_name;
            """

        let! result = conn.QueryAsync<string>(sql, {| jobName = jobName; scheduledTime = scheduledTime; podId = podId |})
        return Seq.length result > 0
    }

/// Marks a scheduled job as completed and releases the lock.
let completeScheduledJob (jobName: string): Task =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
UPDATE scheduled_job 
SET last_completed_at = NOW(),
    locked_until = NULL,
    locked_by = NULL
WHERE job_name = @jobName;
            """

        let! _ = conn.ExecuteAsync(sql, {| jobName = jobName |})
        return ()
    }

/// Records an LLM triage verdict via event stream.
let insertLlmTriage
    (chatId: int64) (messageId: int) (_userId: int64)
    (verdict: string) (promptTokens: int) (completionTokens: int) (latencyMs: int) : Task =
    recordLlmClassified chatId messageId verdict promptTokens completionTokens latencyMs None None

/// Gets LLM triage stats from the event table, joined with VahterActed events for accuracy.
let getLlmTriageStats (interval: TimeSpan option) : Task<LlmTriageStats> =
    task {
        use conn = new NpgsqlConnection(connString)

        //language=postgresql
        let sql =
            """
SELECT
    lt.data->>'verdict'                                                   AS "LlmVerdict",
    COALESCE(
        CASE WHEN jsonb_typeof(va.data->'actionType') = 'object'
             THEN va.data->'actionType'->>'Case'
             ELSE va.data->>'actionType'
        END,
        '(pending)')                                                        AS "VahterAction",
    COUNT(*)::INT                                                          AS "Count",
    COALESCE(SUM((lt.data->>'promptTokens')::INT + (lt.data->>'completionTokens')::INT), 0) AS "TotalTokens",
    COALESCE(AVG((lt.data->>'latencyMs')::INT), 0)                         AS "AvgLatencyMs"
FROM event lt
LEFT JOIN event va
       ON va.event_type = 'VahterActed'
      AND va.data->>'chatId'    = lt.data->>'chatId'
      AND va.data->>'messageId' = lt.data->>'messageId'
WHERE lt.event_type = 'LlmClassified'
  AND (@interval::INTERVAL IS NULL OR lt.created_at > NOW() - @interval::INTERVAL)
GROUP BY lt.data->>'verdict',
    CASE WHEN jsonb_typeof(va.data->'actionType') = 'object'
         THEN va.data->'actionType'->>'Case'
         ELSE va.data->>'actionType'
    END
ORDER BY "Count" DESC
            """

        let! rows = conn.QueryAsync<LlmTriageRow>(sql, {| interval = interval |})
        return { interval = interval; rows = Array.ofSeq rows }
    }

let loadBotSettings () =
    task {
        use conn = new NpgsqlConnection(connString)
        let! rows = conn.QueryAsync<{| key: string; value: string |}>(
            "SELECT key, value FROM bot_setting WHERE value IS NOT NULL")
        return rows |> Seq.map (fun r -> r.key, r.value) |> readOnlyDict
    }

/// Executes an action while holding a PostgreSQL session-level advisory lock.
/// Returns true if the lock was acquired and the action completed.
/// Returns false if the lock is already held by another session.
/// The lock is explicitly released after the action, or automatically released
/// if the connection drops (e.g., pod crash).
let withAdvisoryLock (lockKey: int) (action: unit -> Task) : Task<bool> =
    task {
        use conn = new NpgsqlConnection(connString)
        do! conn.OpenAsync()

        //language=postgresql
        let! acquired = conn.QuerySingleAsync<bool>("SELECT pg_try_advisory_lock(@key)", {| key = lockKey |})
        if acquired then
            try
                do! action()
                return true
            finally
                // Explicitly release; also auto-released if connection drops
                conn.Execute("SELECT pg_advisory_unlock(@key)", {| key = lockKey |}) |> ignore
        else
            return false
    }
