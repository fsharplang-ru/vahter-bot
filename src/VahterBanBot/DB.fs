namespace VahterBanBot

open System
open System.Data
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.ML.Data
open Npgsql
open NpgsqlTypes
open VahterBanBot.Types
open Dapper
open BotInfra

/// Lightweight DTO for getUserMessages (used for message deletion).
[<CLIMutable>]
type UserMessage =
    { chat_id: int64
      message_id: int }

[<CLIMutable>]
type SpamOrHamDb =
    { text: string
      spam: bool
      less_than_n_messages: bool
      custom_emoji_count: int
      created_at: DateTime }

// ---------------------------------------------------------------------------
// Private event-store helpers (SRTP inline functions can't be class members)
// ---------------------------------------------------------------------------
module private EventStore =

    /// Deserializes data JSON → DU event.
    let deserializeEvent<'Event> (raw: RawEvent) : 'Event =
        JsonSerializer.Deserialize<'Event>(raw.data, options = eventJsonOpts)

    /// SRTP helper — folds raw events into aggregate state using Fold/Zero from the state type.
    let inline foldEvents<'Event, 'State
        when 'State : (static member Zero : 'State)
        and  'State : (static member Fold : 'State * 'Event -> 'State)>
        (rawEvents: RawEvent list) : 'State =
        let fold s e = 'State.Fold(s, e)
        rawEvents |> List.map deserializeEvent<'Event> |> List.fold fold 'State.Zero

    /// Reads all events for a stream; returns (events, currentVersion) where
    /// currentVersion = 0 means the stream does not exist yet.
    let readStream (connString: string) (streamId: string) : Task<RawEvent list * int> =
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
    let tryAppend<'Event> (connString: string) (streamId: string) (expectedVersion: int) (events: 'Event list) : Task<Result<unit, ConcurrencyConflict>> =
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
                let data = JsonSerializer.Serialize<'Event>(e, eventJsonOpts)
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
        (connString:  string)
        (fold:        'State -> 'Event -> 'State)
        (initial:     'State)
        (decider:     'State -> 'Event list)
        (streamId:    string)
        : Task<'Event list * 'State> =
        task {
            let mutable result = ValueNone
            while result.IsNone do
                let! (rawEvents, version) = readStream connString streamId
                let state     = rawEvents |> List.map deserializeEvent<'Event> |> List.fold fold initial
                let newEvents = decider state
                if newEvents.IsEmpty then
                    result <- ValueSome ([], state)
                else
                    match! tryAppend connString streamId version newEvents with
                    | Ok _                    ->
                        let finalState = newEvents |> List.fold fold state
                        result <- ValueSome (newEvents, finalState)
                    | Error ConcurrencyConflict -> ()   // re-read, re-decide
            return result.Value
        }

    /// SRTP wrapper — resolves Fold/Zero from the state type at compile time.
    let inline appendEvent (connString: string) (streamId: string) (decider: 'State -> 'Event list) : Task<'Event list * 'State>
        when 'State : (static member Zero : 'State)
        and  'State : (static member Fold : 'State * 'Event -> 'State) =
        let fold s e = 'State.Fold(s, e)
        transact connString fold 'State.Zero decider streamId

type DbService(connString: string, timeProvider: TimeProvider) =
    let utcNow () = timeProvider.GetUtcNow().UtcDateTime

    // -----------------------------------------------------------------------
    // Private helpers (called by multiple public members)
    // -----------------------------------------------------------------------

    let recordUsernameChanged (userId: int64) (username: string option) : Task<unit> =
        task {
            let! _ = EventStore.appendEvent connString $"user:{userId}" (fun state ->
                if state.Username = username then []
                else [ UsernameChanged {| userId = userId; username = username |} ])
            return ()
        }

    let recordUserReaction (userId: int64) (username: string option) (reactionIncrement: int) : Task<User> =
        task {
            let! (_, state) = EventStore.appendEvent connString $"user:{userId}" (fun state ->
                let usernameEvt =
                    if state.Username = username then []
                    else [ UsernameChanged {| userId = userId; username = username |} ]
                usernameEvt @ [ UserReactionRecorded {| userId = userId; delta = reactionIncrement |} ])
            return state
        }

    let recordUserBannedImpl (userId: int64) (actor: Actor) (chatId: int64 option) (messageId: int option) (messageText: string option) (banExpiryDays: int) : Task<unit> =
        task {
            let! _ = EventStore.appendEvent connString $"user:{userId}" (fun (state: User) ->
                if state.IsBanned(banExpiryDays, utcNow()) then []   // idempotent — already banned
                else [ UserBanned {| userId = userId; bannedBy = None; actor = Some actor
                                     chatId = chatId; messageId = messageId
                                     messageText = messageText; bannedAt = utcNow() |} ])
            return ()
        }

    let recordMessageReceived (chatId: int64) (messageId: int) (userId: int64) (text: string option) (rawMessage: string) : Task<unit> =
        task {
            let! _ = EventStore.appendEvent connString $"message:{chatId}:{messageId}" (fun state ->
                if state.Received then []
                else [ MessageReceived {| chatId = chatId; messageId = messageId; userId = userId; text = text; rawMessage = rawMessage |} ])
            return ()
        }

    let recordMessageEdited (chatId: int64) (messageId: int) (userId: int64) (text: string option) (rawMessage: string) : Task<unit> =
        task {
            let! _ = EventStore.appendEvent connString $"message:{chatId}:{messageId}" (fun (_: Message) ->
                [ MessageEdited {| chatId = chatId; messageId = messageId; userId = userId; text = text; rawMessage = rawMessage |} ])
            return ()
        }

    let recordVahterAction
        (vahterId: int64) (actionType: VahterAction) (targetUserId: int64)
        (chatId: int64) (messageId: int) : Task<bool> =
        task {
            let! (_, state) = EventStore.appendEvent connString $"moderation:{chatId}:{messageId}" (fun (_: Moderation) ->
                [ VahterActed {| vahterId = vahterId; actionType = actionType; targetUserId = targetUserId; chatId = chatId; messageId = messageId |} ])
            return state.VahterActedCount <= 1
        }

    let expireCallbackImpl (callbackId: Guid) : Task<unit> =
        task {
            let! _ = EventStore.appendEvent connString $"callback:{callbackId}" (fun (state: Callback) ->
                if state.IsTerminal then []
                else [ CallbackExpired ])
            return ()
        }

    // -----------------------------------------------------------------------
    // Public members — User operations
    // -----------------------------------------------------------------------

    member _.UpsertUser(userId: int64, username: string option) : Task<User> =
        task {
            let! (_, state) = EventStore.appendEvent connString $"user:{userId}" (fun (state: User) ->
                if state.Username = username then []
                else [ UsernameChanged {| userId = userId; username = username |} ])
            return { state with Id = userId }
        }

    member _.UpsertUserAndIncrementReactions(userId: int64, username: string option, reactionIncrement: int) : Task<User> =
        task {
            let! state = recordUserReaction userId username reactionIncrement
            return { state with Id = userId }
        }

    /// Records a UserBanned event from a TgMessage.
    member _.RecordUserBanned(actor: Actor, msg: TgMessage, banExpiryDays: int) : Task<unit> =
        recordUserBannedImpl msg.SenderId actor (Some msg.ChatId) (Some msg.MessageId) (Option.ofObj msg.Text) banExpiryDays

    /// Records a UserBanned event without a TgMessage (e.g. reaction spam).
    member _.RecordUserBannedNoMessage(userId: int64, actor: Actor, chatId: int64, messageId: int, banExpiryDays: int) : Task<unit> =
        recordUserBannedImpl userId actor (Some chatId) (Some messageId) None banExpiryDays

    /// Records a UserUnbanned event with the new Actor format.
    member _.RecordUserUnbanned(userId: int64, actor: Actor) : Task<unit> =
        task {
            let! _ = EventStore.appendEvent connString $"user:{userId}" (fun (state: User) ->
                if state.Banned.IsNone then []
                else [ UserUnbanned {| userId = userId; unbannedBy = None; actor = Some actor |} ])
            return ()
        }

    member _.GetUserById(userId: int64) : Task<User option> =
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
                let state : User = EventStore.foldEvents<UserEvent, User> events
                return Some { state with Id = userId }
        }

    // -----------------------------------------------------------------------
    // Public members — Message operations
    // -----------------------------------------------------------------------

    member _.InsertMessage(msg: TgMessage) : Task =
        task {
            do! recordUsernameChanged msg.SenderId (Option.ofObj msg.SenderUsername)
            do! recordMessageReceived msg.ChatId msg.MessageId msg.SenderId
                    (Option.ofObj msg.Text)
                    msg.RawJson
        }

    member _.EditMessage(msg: TgMessage) : Task =
        task {
            do! recordUsernameChanged msg.SenderId (Option.ofObj msg.SenderUsername)
            do! recordMessageEdited msg.ChatId msg.MessageId msg.SenderId
                    (Option.ofObj msg.Text)
                    msg.RawJson
        }

    member _.GetUserMessages(userId: int64) : Task<UserMessage array> =
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

    /// Records a MessageMarkedHam event. Latest Spam/Ham decision wins.
    member _.RecordMessageMarkedHam(chatId: int64, messageId: int, text: string, markedBy: int64 option) : Task<unit> =
        task {
            let! _ = EventStore.appendEvent connString $"message:{chatId}:{messageId}" (fun state ->
                if state.Classification = SpamClassification.Ham then []   // already ham
                else [ MessageMarkedHam {| chatId = chatId; messageId = messageId; text = text; markedBy = markedBy |} ])
            return ()
        }

    /// Records a MessageMarkedSpam event. Latest Spam/Ham decision wins.
    member _.RecordMessageMarkedSpam(chatId: int64, messageId: int, markedBy: int64 option) : Task<unit> =
        task {
            let! _ = EventStore.appendEvent connString $"message:{chatId}:{messageId}" (fun state ->
                if state.Classification = SpamClassification.Spam then []  // already spam
                else [ MessageMarkedSpam {| chatId = chatId; messageId = messageId; markedBy = markedBy |} ])
            return ()
        }

    member _.CountUniqueUserMsg(userId: int64) : Task<int> =
        task {
            use conn = new NpgsqlConnection(connString)

            //language=postgresql
            let sql =
                """
SELECT COUNT(DISTINCT msg_text_md5)::INT
FROM event
WHERE event_type = 'MessageReceived'
  AND (data->>'userId')::BIGINT = @userId
                """

            let! result = conn.QuerySingleAsync<int>(sql, {| userId = userId |})
            return result
        }

    member _.GetUserStatsByLastNMessages(n: int, userId: int64) : Task<UserStats> =
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
                 AND e2.data->'actionType'->>'Case' IN ('PotentialKill', 'ManualBan')
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

    // -----------------------------------------------------------------------
    // Public members — Moderation / Detection operations
    // -----------------------------------------------------------------------

    /// Records a VahterActed event. Returns true if first action (used for deduplication).
    member _.TryRecordVahterAction(vahterId: int64, actionType: VahterAction, targetUserId: int64, chatId: int64, msgId: int) : Task<bool> =
        recordVahterAction vahterId actionType targetUserId chatId msgId

    /// Records a BotAutoDeleted event. NOT idempotent — each call adds an event.
    member _.RecordBotAutoDeleted(chatId: int64, messageId: int, userId: int64, reason: AutoDeleteReason) : Task<unit> =
        task {
            let! _ = EventStore.appendEvent connString $"moderation:{chatId}:{messageId}" (fun (_: Moderation) ->
                [ BotAutoDeleted {| chatId = chatId; messageId = messageId; userId = userId; reason = reason |} ])
            return ()
        }

    /// Records an MlScoredMessage event for observability and determinism testing.
    member _.RecordMlScoredMessage(chatId: int64, messageId: int, score: float, isSpam: bool) : Task<unit> =
        task {
            let! _ = EventStore.appendEvent connString $"detection:{chatId}:{messageId}" (fun (_: Detection) ->
                [ MlScoredMessage {| chatId = chatId; messageId = messageId; score = score; isSpam = isSpam |} ])
            return ()
        }

    /// Records an LlmClassified event.
    member _.RecordLlmClassified
        (chatId: int64, messageId: int, verdict: string,
         promptTokens: int, completionTokens: int, latencyMs: int,
         modelName: string option, promptHash: string option) : Task<unit> =
        task {
            let! _ = EventStore.appendEvent connString $"detection:{chatId}:{messageId}" (fun (_: Detection) ->
                [ LlmClassified {| chatId = chatId; messageId = messageId; verdict = verdict
                                   promptTokens = promptTokens; completionTokens = completionTokens; latencyMs = latencyMs
                                   modelName = modelName; promptHash = promptHash |} ])
            return ()
        }

    // -----------------------------------------------------------------------
    // Public members — Callback operations
    // -----------------------------------------------------------------------

    /// Creates a callback by appending a CallbackCreated event.
    member _.RecordCallbackCreated(callbackId: Guid, data: CallbackMessage, targetUserId: int64, channelId: int64) : Task<unit> =
        task {
            let serializedData = serializeCallbackData data
            let! _ = EventStore.appendEvent connString $"callback:{callbackId}" (fun (_: Callback) ->
                [ CallbackCreated {| data = serializedData; targetUserId = targetUserId; actionChannelId = channelId |} ])
            return ()
        }

    /// Records the action message ID after posting to channel.
    member _.RecordCallbackMessagePosted(callbackId: Guid, messageId: int) : Task<unit> =
        task {
            let! _ = EventStore.appendEvent connString $"callback:{callbackId}" (fun (state: Callback) ->
                if state.IsTerminal || state.ActionMessageId.IsSome then []
                else [ CallbackMessagePosted {| actionMessageId = messageId |} ])
            return ()
        }

    /// Atomically resolves a callback (vahter clicked a button).
    /// Returns Some aggregate state if resolved, None if already terminal.
    member _.ResolveCallback(callbackId: Guid) : Task<Callback option> =
        task {
            let! (events, state) = EventStore.appendEvent connString $"callback:{callbackId}" (fun (state: Callback) ->
                if state.IsTerminal || state.Data.IsNone then []
                else [ CallbackResolved ])
            return if events.IsEmpty then None else Some state
        }

    /// Expires a callback (cleanup/orphaned — no vahter action taken).
    member _.ExpireCallback(callbackId: Guid) : Task<unit> =
        expireCallbackImpl callbackId

    /// Gets all active (non-terminal) callbacks for a user.
    member _.GetActiveCallbacksByUserId(userId: int64) : Task<ActiveCallbackInfo array> =
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
    member _.GetFailedCallbackPosts(age: TimeSpan) : Task<Guid array> =
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

            let! result = conn.QueryAsync<Guid>(sql, {| cutoff = utcNow().Subtract age |})
            return Array.ofSeq result
        }

    /// Gets active callbacks in a specific channel, older than the given age.
    member _.GetOldCallbacksInChannel(age: TimeSpan, channelId: int64) : Task<ActiveCallbackInfo array> =
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

            let! result = conn.QueryAsync<ActiveCallbackInfo>(sql, {| channelId = channelId; cutoff = utcNow().Subtract age |})
            return Array.ofSeq result
        }

    /// Expires all callbacks sharing the same action message ID (sibling buttons).
    member _.ExpireCallbacksByMessageId(actionMessageId: int) : Task<unit> =
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
                do! expireCallbackImpl callbackId
        }

    /// Expires orphaned callbacks older than the given age.
    member _.ExpireOrphanedCallbacks(howOld: TimeSpan) : Task<int> =
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

            let! orphanedIds = conn.QueryAsync<Guid>(sql, {| cutoff = utcNow().Subtract howOld |})
            let ids = Array.ofSeq orphanedIds
            for callbackId in ids do
                do! expireCallbackImpl callbackId
            return ids.Length
        }

    // -----------------------------------------------------------------------
    // Public members — Stats / Queries
    // -----------------------------------------------------------------------

    member _.GetVahterStats(banInterval: TimeSpan option) : Task<VahterStats> =
        task {
            use conn = new NpgsqlConnection(connString)

            //language=postgresql
            let sql =
                """
SELECT * FROM (
    SELECT vahter.username                                                      AS "Vahter"
          , COUNT(*)                                                             AS "KillCountTotal"
          , COUNT(*) FILTER (WHERE b.banned_at > @now - @banInterval::INTERVAL) AS "KillCountInterval"
     FROM banned b
              JOIN "user" vahter ON vahter.id = b.banned_by
     GROUP BY b.banned_by, vahter.username
     UNION
     SELECT 'bot'                                                          AS "Vahter",
            COUNT(*)                                                       AS "KillCountTotal",
            COUNT(*) FILTER (WHERE bbb.banned_at > @now - @banInterval::INTERVAL) AS "KillCountInterval"
     FROM (SELECT banned_user_id, MIN(banned_at) AS banned_at
           FROM banned_by_bot
           GROUP BY banned_user_id) bbb
) stats
ORDER BY "KillCountTotal" DESC;
                """

            let! stats = conn.QueryAsync<VahterStat>(sql, {| banInterval = banInterval; now = utcNow() |})
            return { VahterStats.interval = banInterval; stats = Array.ofSeq stats }
        }

    /// Gets vahter action stats from event table
    member _.GetVahterActionStats(interval: TimeSpan option) : Task<VahterActionStats> =
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
           COUNT(*) FILTER (WHERE va.data->'actionType'->>'Case' IN ('PotentialKill', 'ManualBan')) AS "KillsTotal",
           COUNT(*) FILTER (WHERE va.data->'actionType'->>'Case' IN ('PotentialKill', 'ManualBan')
                              AND va.created_at > @now - @interval::INTERVAL) AS "KillsInterval",
           COUNT(*) FILTER (WHERE va.data->'actionType'->>'Case' IN ('PotentialNotSpam', 'DetectedNotSpam')) AS "NotSpamTotal",
           COUNT(*) FILTER (WHERE va.data->'actionType'->>'Case' IN ('PotentialNotSpam', 'DetectedNotSpam')
                              AND va.created_at > @now - @interval::INTERVAL) AS "NotSpamInterval"
    FROM event va
    WHERE va.event_type = 'VahterActed'
    GROUP BY va.data->>'vahterId'
) va_stats
ORDER BY va_stats."KillsTotal" + va_stats."NotSpamTotal" DESC;
                """

            let! stats = conn.QueryAsync<VahterActionStat>(sql, {| interval = interval; now = utcNow() |})
            return { interval = interval; stats = Array.ofSeq stats }
        }

    // -----------------------------------------------------------------------
    // Public members — ML operations
    // -----------------------------------------------------------------------

    member _.MlData(criticalMsgCount: int, criticalDate: DateTime) : Task<SpamOrHamDb array> =
        task {
            use conn = new NpgsqlConnection(connString)

            //language=postgresql
            let sql =
                """
WITH final_messages AS (
    -- Latest text per message (edits override original)
    SELECT DISTINCT ON (stream_id)
        stream_id,
        (data->>'chatId')::BIGINT              AS chat_id,
        (data->>'messageId')::INT              AS message_id,
        (data->>'userId')::BIGINT              AS user_id,
        data->>'text'                          AS text,
        data->'rawMessage'->'entities'         AS entities,
        created_at
    FROM event
    WHERE event_type IN ('MessageReceived', 'MessageEdited')
      AND data->>'text' IS NOT NULL
      AND created_at >= @criticalDate
    ORDER BY stream_id, id DESC
),
user_msg_counts AS (
    SELECT user_id,
           COUNT(DISTINCT text) < @criticalMsgCount AS less_than_n_messages
    FROM final_messages
    GROUP BY user_id
),
verdicts AS (
    -- All verdict-bearing events, unified across message and moderation streams
    SELECT
        (data->>'chatId')::BIGINT  AS chat_id,
        (data->>'messageId')::INT  AS message_id,
        id                         AS event_id,
        CASE
            WHEN event_type = 'BotAutoDeleted' THEN TRUE
            WHEN event_type = 'VahterActed'
                 AND data->'actionType'->>'Case' IN ('PotentialKill', 'ManualBan') THEN TRUE
            WHEN event_type = 'VahterActed'
                 AND data->'actionType'->>'Case' IN ('PotentialNotSpam', 'DetectedNotSpam') THEN FALSE
            WHEN event_type = 'MessageMarkedSpam' THEN TRUE
            WHEN event_type = 'MessageMarkedHam'  THEN FALSE
        END AS is_spam
    FROM event
    WHERE event_type IN ('BotAutoDeleted', 'VahterActed', 'MessageMarkedSpam', 'MessageMarkedHam')
),
last_verdict AS (
    -- Last decisive verdict per message (highest event id wins)
    SELECT DISTINCT ON (chat_id, message_id)
        chat_id, message_id, is_spam
    FROM verdicts
    WHERE is_spam IS NOT NULL
    ORDER BY chat_id, message_id, event_id DESC
)
SELECT m.text,
       COALESCE(v.is_spam, FALSE)                                          AS spam,
       COALESCE(u.less_than_n_messages, TRUE)                              AS less_than_n_messages,
       (SELECT COUNT(*) FROM jsonb_array_elements(m.entities) ent
        WHERE ent->>'type' = 'custom_emoji')::INT                          AS custom_emoji_count,
       MAX(m.created_at)                                                   AS created_at
FROM final_messages m
LEFT JOIN last_verdict v ON v.chat_id = m.chat_id AND v.message_id = m.message_id
LEFT JOIN user_msg_counts u ON u.user_id = m.user_id
GROUP BY m.text, v.is_spam, u.less_than_n_messages, m.entities
ORDER BY MAX(m.created_at);
"""

            let! data = conn.QueryAsync<SpamOrHamDb>(sql, {| criticalDate = criticalDate; criticalMsgCount = criticalMsgCount |})
            return Array.ofSeq data
        }

    /// Saves a trained ML model to the database (singleton row, upsert).
    member _.SaveTrainedModel(modelStream: Stream) : Task =
        task {
            use conn = new NpgsqlConnection(connString)
            do! conn.OpenAsync()

            //language=postgresql
            let sql =
                """
INSERT INTO ml_trained_model (id, model_data, created_at)
VALUES (1, @modelData, @now)
ON CONFLICT (id) DO UPDATE
    SET model_data = EXCLUDED.model_data,
        created_at = EXCLUDED.created_at;
                """

            use cmd = new NpgsqlCommand(sql, conn)
            cmd.Parameters.Add(NpgsqlParameter("modelData", NpgsqlTypes.NpgsqlDbType.Bytea, Value = modelStream)) |> ignore
            cmd.Parameters.AddWithValue("now", utcNow()) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    /// Loads a trained ML model from the database as a Stream.
    member _.WithTrainedModel(action: Stream * DateTime -> Task<'a>) : Task<'a option> =
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
    member _.GetModelCreatedAt() : Task<DateTime option> =
        task {
            use conn = new NpgsqlConnection(connString)

            //language=postgresql
            let sql = "SELECT created_at FROM ml_trained_model WHERE id = 1"

            let! result = conn.QueryAsync<DateTime>(sql)
            return Seq.tryHead result
        }

    // -----------------------------------------------------------------------
    // Public members — Scheduler operations
    // -----------------------------------------------------------------------

    /// Tries to acquire a scheduled job with lease mechanism.
    member _.TryAcquireScheduledJob(jobName: string, scheduledTime: TimeSpan, podId: string) : Task<bool> =
        task {
            use conn = new NpgsqlConnection(connString)

            //language=postgresql
            let sql =
                """
UPDATE scheduled_job
SET locked_until = @now + INTERVAL '1 hour',
    locked_by = @podId
WHERE job_name = @jobName
  AND @now >= (CURRENT_DATE + @scheduledTime)
  AND (last_completed_at IS NULL OR last_completed_at < (CURRENT_DATE + @scheduledTime))
  AND (locked_until IS NULL OR locked_until < @now)
RETURNING job_name;
                """

            let! result = conn.QueryAsync<string>(sql, {| jobName = jobName; scheduledTime = scheduledTime; podId = podId; now = utcNow() |})
            return Seq.length result > 0
        }

    /// Marks a scheduled job as completed and releases the lock.
    member _.CompleteScheduledJob(jobName: string) : Task =
        task {
            use conn = new NpgsqlConnection(connString)

            //language=postgresql
            let sql =
                """
UPDATE scheduled_job
SET last_completed_at = @now,
    locked_until = NULL,
    locked_by = NULL
WHERE job_name = @jobName;
                """

            let! _ = conn.ExecuteAsync(sql, {| jobName = jobName; now = utcNow() |})
            return ()
        }

    /// Executes an action while holding a PostgreSQL session-level advisory lock.
    member _.WithAdvisoryLock(lockKey: int, action: unit -> Task) : Task<bool> =
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
