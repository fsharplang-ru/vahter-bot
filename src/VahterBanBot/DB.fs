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

/// Lightweight DTO for the reaction-spam triage dossier (recent activity from one user).
[<CLIMutable>]
type DossierEvent =
    { chat_id: int64
      message_id: int
      kind: string          // "message" or "reaction"
      text: string          // message text (NULL for reaction)
      emoji: string         // joined emoji string for reaction events (NULL for message)
      created_at: DateTime }

/// Lightweight DTO for cached user profile (photo + bio).
[<CLIMutable>]
type CachedUserProfile =
    { photo_bytes: byte[]
      bio: string
      fetched_at: DateTime }

[<CLIMutable>]
type SpamOrHamDb =
    { text: string
      spam: bool
      less_than_n_messages: bool
      custom_emoji_count: int
      created_at: DateTime }

type DbService(connString: string, timeProvider: TimeProvider) =
    let utcNow () = timeProvider.GetUtcNow().UtcDateTime
    let store = EventStore(connString, "event", eventJsonOpts)

    // -----------------------------------------------------------------------
    // Snapshot read-model upserts (run in the SAME TX as the event append).
    // stream_version / created_at are read back from the event table inside the TX —
    // the just-inserted rows are visible — so callers never thread the version through.
    // Each *_version guard keeps the upsert monotonic and (for snapshot_message) lets the
    // message:* and moderation:* streams write their own column in any order.
    // -----------------------------------------------------------------------

    let upsertUserSnapshot (userId: int64) (stateJson: string) (conn: NpgsqlConnection) (tx: NpgsqlTransaction) : Task =
        //language=postgresql
        let sql =
            """
INSERT INTO snapshot_user (user_id, stream_version, state, created_at)
SELECT @userId, COALESCE(MAX(stream_version), 0), @state::jsonb, MIN(created_at)
FROM event WHERE stream_id = @sid
ON CONFLICT (user_id) DO UPDATE
   SET stream_version = EXCLUDED.stream_version, state = EXCLUDED.state, updated_at = now()
 WHERE snapshot_user.stream_version <= EXCLUDED.stream_version
            """
        conn.ExecuteAsync(sql, {| userId = userId; state = stateJson; sid = $"user:{userId}" |}, tx) :> Task

    // Recompute a message's snapshot row from BOTH of its streams (message:* + moderation:*) and
    // write it in one TX. message_data.classification is the cross-stream spam/ham status produced by
    // Message.FoldTimeline (last decisive event wins, ordered by (created_at, id)); moderation_data is
    // the moderation-stream fold. The row is locked first, so concurrent message/moderation writers and
    // the rebuild serialize and each recomputes from the fully-committed log — no version-guard races.
    // All verdict logic lives in F#; the SQL here is static and parameterized.
    let upsertMessageSnapshot (chatId: int64) (messageId: int) (conn: NpgsqlConnection) (tx: NpgsqlTransaction) : Task =
        task {
            let msgSid = $"message:{chatId}:{messageId}"
            let modSid = $"moderation:{chatId}:{messageId}"

            // 1. ensure the row exists and lock it for this TX (DO UPDATE takes a row lock).
            //language=postgresql
            let lockSql =
                """
INSERT INTO snapshot_message (chat_id, message_id) VALUES (@chatId, @messageId)
ON CONFLICT (chat_id, message_id) DO UPDATE SET updated_at = snapshot_message.updated_at
                """
            let! _ = conn.ExecuteAsync(lockSql, {| chatId = chatId; messageId = messageId |}, tx)

            // 2. read both streams on this TX in ONE round-trip — sees others' committed events
            //    plus our own just-appended ones.
            let! streams = store.ReadRawEventsForStreams(conn, tx, [ msgSid; modSid ])
            let msgRaws = streams[msgSid]
            let modRaws = streams[modSid]

            // 3. fold: message status from the merged, time-ordered timeline; moderation from its stream.
            let timeline =
                [ for r in msgRaws -> r.created_at, r.id, FromMessage (store.Deserialize<MessageEvent> r)
                  for r in modRaws -> r.created_at, r.id, FromModeration (store.Deserialize<ModerationEvent> r) ]
                |> List.sortBy (fun (ts, id, _) -> ts, id)
            let message = timeline |> List.fold (fun s (_, _, ev) -> Message.FoldTimeline(s, ev)) Message.Zero
            let moderation = modRaws |> List.fold (fun s r -> Moderation.Fold(s, store.Deserialize<ModerationEvent> r)) Moderation.Zero

            let messageData    = if List.isEmpty msgRaws then null else JsonSerializer.Serialize(messageSnapshot message, snapshotJsonOpts)
            let moderationData = if List.isEmpty modRaws then null else JsonSerializer.Serialize(moderationSnapshot moderation, snapshotJsonOpts)
            let msgVersion = msgRaws |> List.tryLast |> Option.map (fun r -> r.stream_version) |> Option.toNullable
            let modVersion = modRaws |> List.tryLast |> Option.map (fun r -> r.stream_version) |> Option.toNullable
            let createdAt  = msgRaws |> List.tryHead |> Option.map (fun r -> r.created_at) |> Option.toNullable   // receipt time

            // 4. write the recomputed row (we hold the lock, so an unconditional update is correct).
            //language=postgresql
            let updateSql =
                """
UPDATE snapshot_message
   SET message_data    = @messageData::jsonb,
       moderation_data = @moderationData::jsonb,
       msg_version     = @msgVersion,
       mod_version     = @modVersion,
       created_at      = @createdAt,
       updated_at      = now()
 WHERE chat_id = @chatId AND message_id = @messageId
                """
            let! _ =
                conn.ExecuteAsync(updateSql,
                    {| chatId = chatId; messageId = messageId
                       messageData = messageData; moderationData = moderationData
                       msgVersion = msgVersion; modVersion = modVersion; createdAt = createdAt |}, tx)
            return ()
        } :> Task

    // Append wrappers: fold the new events to final state, serialize the snapshot DTO,
    // and attach the matching upsert as the in-TX projection.
    let appendUserEvents (userId: int64) (decide: User -> UserEvent list) : Task<UserEvent list * User> =
        EventStore.appendEventWithProjection store $"user:{userId}" (fun (state: User) ->
            match decide state with
            | [] -> [], None
            | evts ->
                let final = evts |> List.fold (fun s e -> User.Fold(s, e)) state
                let json = JsonSerializer.Serialize(userSnapshot final, snapshotJsonOpts)
                evts, Some (upsertUserSnapshot userId json))

    // The snapshot row is recomputed from BOTH streams by upsertMessageSnapshot, so both append
    // paths attach the same projection. The decider's folded state is only used for its decision /
    // the returned value; the snapshot itself is rebuilt from the log in the projection.
    let appendMessageEvents (chatId: int64) (messageId: int) (decide: Message -> MessageEvent list) : Task<MessageEvent list * Message> =
        EventStore.appendEventWithProjection store $"message:{chatId}:{messageId}" (fun (state: Message) ->
            match decide state with
            | [] -> [], None
            | evts -> evts, Some (upsertMessageSnapshot chatId messageId))

    let appendModerationEvents (chatId: int64) (messageId: int) (decide: Moderation -> ModerationEvent list) : Task<ModerationEvent list * Moderation> =
        EventStore.appendEventWithProjection store $"moderation:{chatId}:{messageId}" (fun (state: Moderation) ->
            match decide state with
            | [] -> [], None
            | evts -> evts, Some (upsertMessageSnapshot chatId messageId))

    // -----------------------------------------------------------------------
    // Private helpers (called by multiple public members)
    // -----------------------------------------------------------------------

    let recordUsernameChanged (userId: int64) (username: string option) : Task<unit> =
        task {
            let! _ = appendUserEvents userId (fun state ->
                if state.Username = username then []
                else [ UsernameChanged {| userId = userId; username = username |} ])
            return ()
        }

    let recordUserReaction (userId: int64) (username: string option) (chatId: int64) (messageId: int) (emoji: string option) (reactionIncrement: int) : Task<User> =
        task {
            let! (_, state) = appendUserEvents userId (fun state ->
                let usernameEvt =
                    if state.Username = username then []
                    else [ UsernameChanged {| userId = userId; username = username |} ]
                usernameEvt @ [ UserReactionRecorded {| userId = userId; chatId = Some chatId; messageId = Some messageId; emoji = emoji; delta = reactionIncrement |} ])
            return state
        }

    let recordReactionTriageNotSpamSet (userId: int64) (until: DateTime) (actor: Actor) : Task<unit> =
        task {
            let! _ = appendUserEvents userId (fun (_: User) ->
                [ ReactionTriageNotSpamSet {| userId = userId; until = until; actor = actor |} ])
            return ()
        }

    let recordUserBannedImpl (userId: int64) (actor: Actor) (chatId: int64 option) (messageId: int option) (messageText: string option) (banExpiryDays: int) : Task<unit> =
        task {
            let! _ = appendUserEvents userId (fun (state: User) ->
                if state.IsBanned(banExpiryDays, utcNow()) then []   // idempotent — already banned
                else [ UserBanned {| userId = userId; bannedBy = None; actor = Some actor
                                     chatId = chatId; messageId = messageId
                                     messageText = messageText; bannedAt = utcNow() |} ])
            return ()
        }

    let recordMessageReceived (chatId: int64) (messageId: int) (userId: int64) (text: string option) (rawMessage: string) : Task<unit> =
        task {
            let! _ = EventStore.appendEventWithProjection store $"message:{chatId}:{messageId}" (fun (state: Message) ->
                if state.Received then [], None
                else
                    // Store rawMessage as a JSON string (the live wire shape); JsonElement just makes
                    // *reading* tolerant of the legacy object shape too (issue #166).
                    let evt = MessageReceived {| chatId = chatId; messageId = messageId; userId = userId; text = text; rawMessage = JsonSerializer.SerializeToElement(rawMessage, eventJsonOpts) |}
                    // One TX: optional text-index upsert, then the snapshot recompute.
                    let projection (conn: NpgsqlConnection) (tx: NpgsqlTransaction) =
                        task {
                            match text with
                            | Some t when not (String.IsNullOrEmpty t) ->
                                let sql =
                                    """
INSERT INTO user_msg_text_index (user_id, msg_text_md5)
VALUES (@userId, md5(@text))
ON CONFLICT DO NOTHING
                                    """
                                let! _ = conn.ExecuteAsync(sql, {| userId = userId; text = t |}, tx)
                                ()
                            | _ -> ()
                            do! upsertMessageSnapshot chatId messageId conn tx
                        } :> Task
                    [ evt ], Some projection)
            return ()
        }

    let recordMessageEdited (chatId: int64) (messageId: int) (userId: int64) (text: string option) (rawMessage: string) : Task<unit> =
        task {
            let! _ = appendMessageEvents chatId messageId (fun (_: Message) ->
                [ MessageEdited {| chatId = chatId; messageId = messageId; userId = userId; text = text; rawMessage = JsonSerializer.SerializeToElement(rawMessage, eventJsonOpts) |} ])
            return ()
        }

    let recordVahterAction
        (vahterId: int64) (actionType: VahterAction) (targetUserId: int64)
        (chatId: int64) (messageId: int) : Task<bool> =
        task {
            let! (_, state) = appendModerationEvents chatId messageId (fun (_: Moderation) ->
                [ VahterActed {| vahterId = vahterId; actionType = actionType; targetUserId = targetUserId; chatId = chatId; messageId = messageId |} ])
            return state.VahterActedCount <= 1
        }

    let expireCallbackImpl (callbackId: Guid) : Task<unit> =
        task {
            let! _ = EventStore.appendEvent store $"callback:{callbackId}" (fun (state: Callback) ->
                if state.IsTerminal then []
                else [ CallbackExpired ])
            return ()
        }

    // -----------------------------------------------------------------------
    // Public members — request-scoped identity map (unit of work)
    // -----------------------------------------------------------------------

    /// Opens a request-scoped event-stream cache for one update handle: repeated loads of the
    /// same aggregate within the returned scope are served from memory instead of re-querying
    /// Postgres. Wrap the per-update entry point in `use _ = db.BeginEventScope()`.
    member _.BeginEventScope() : IDisposable = store.BeginRequestScope()

    // -----------------------------------------------------------------------
    // Public members — User operations
    // -----------------------------------------------------------------------

    member _.UpsertUser(userId: int64, username: string option) : Task<User> =
        task {
            let! (_, state) = appendUserEvents userId (fun (state: User) ->
                if state.Username = username then []
                else [ UsernameChanged {| userId = userId; username = username |} ])
            return { state with Id = userId }
        }

    member _.UpsertUserAndIncrementReactions(userId: int64, username: string option, chatId: int64, messageId: int, emoji: string option, reactionIncrement: int) : Task<User> =
        task {
            let! state = recordUserReaction userId username chatId messageId emoji reactionIncrement
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
            let! _ = appendUserEvents userId (fun (state: User) ->
                if state.Banned.IsNone then []
                else [ UserUnbanned {| userId = userId; unbannedBy = None; actor = Some actor |} ])
            return ()
        }

    member _.GetUserById(userId: int64) : Task<User option> =
        task {
            let! events = store.GetEventsForStream<UserEvent>($"user:{userId}")
            if events.Length = 0 then return None
            else
                let state = (User.Zero, events) ||> Array.fold (fun s e -> User.Fold(s, e))
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

    /// Inserts or updates a single bot_setting value (used by admin commands).
    member _.UpsertBotSetting(key: string, value: string, typ: string, featureGroup: string) : Task<unit> =
        DbSettings.upsertBotSetting connString key value typ featureGroup

    /// Records a MessageMarkedHam event. Latest Spam/Ham decision wins.
    member _.RecordMessageMarkedHam(chatId: int64, messageId: int, text: string, markedBy: int64 option) : Task<unit> =
        task {
            let! _ = appendMessageEvents chatId messageId (fun state ->
                if state.Classification = SpamClassification.Ham then []   // already ham
                else [ MessageMarkedHam {| chatId = chatId; messageId = messageId; text = text; markedBy = markedBy |} ])
            return ()
        }

    /// Records a MessageMarkedSpam event. Latest Spam/Ham decision wins.
    member _.RecordMessageMarkedSpam(chatId: int64, messageId: int, markedBy: int64 option) : Task<unit> =
        task {
            let! _ = appendMessageEvents chatId messageId (fun state ->
                if state.Classification = SpamClassification.Spam then []  // already spam
                else [ MessageMarkedSpam {| chatId = chatId; messageId = messageId; markedBy = markedBy |} ])
            return ()
        }

    /// Returns the number of distinct text-md5s recorded for the user, capped at `cap`.
    /// Callers compare the result against a small threshold, so anything beyond that
    /// threshold is indistinguishable — `LIMIT cap` keeps the cost constant regardless
    /// of how active the user is.
    member _.CountUniqueUserMsgsUpTo(userId: int64, cap: int) : Task<int> =
        task {
            use conn = new NpgsqlConnection(connString)

            //language=postgresql
            let sql =
                """
SELECT COUNT(*)::INT FROM (
    SELECT 1 FROM user_msg_text_index
    WHERE user_id = @userId
    LIMIT @cap
) t
                """

            let! result = conn.QuerySingleAsync<int>(sql, {| userId = userId; cap = cap |})
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
            let! _ = appendModerationEvents chatId messageId (fun (_: Moderation) ->
                [ BotAutoDeleted {| chatId = chatId; messageId = messageId; userId = userId; reason = reason |} ])
            return ()
        }

    /// Records an MlScoredMessage event for observability and determinism testing.
    member _.RecordMlScoredMessage(chatId: int64, messageId: int, score: float, isSpam: bool) : Task<unit> =
        task {
            let! _ = EventStore.appendEvent store $"detection:{chatId}:{messageId}" (fun (_: Detection) ->
                [ MlScoredMessage {| chatId = chatId; messageId = messageId; score = score; isSpam = isSpam |} ])
            return ()
        }

    /// Records an LlmClassified event.
    member _.RecordLlmClassified
        (chatId: int64, messageId: int, verdict: string,
         promptTokens: int, completionTokens: int, latencyMs: int,
         modelName: string option, promptHash: string option) : Task<unit> =
        task {
            let! _ = EventStore.appendEvent store $"detection:{chatId}:{messageId}" (fun (_: Detection) ->
                [ LlmClassified {| chatId = chatId; messageId = messageId; verdict = verdict
                                   promptTokens = promptTokens; completionTokens = completionTokens; latencyMs = latencyMs
                                   modelName = modelName; promptHash = promptHash |} ])
            return ()
        }

    /// Records an LlmReactionTriageClassified event on a user-scoped detection stream.
    /// Stream key: detection:reaction:{userId} — multiple events accumulate (one per threshold trip).
    member _.RecordLlmReactionTriageClassified
        (chatId: int64, userId: int64, verdict: string, reason: string option,
         promptTokens: int, completionTokens: int, latencyMs: int,
         modelName: string option, promptHash: string option, shadowMode: bool) : Task<unit> =
        task {
            let! _ = EventStore.appendEvent store $"detection:reaction:{userId}" (fun (_: Detection) ->
                [ LlmReactionTriageClassified {| chatId = chatId; userId = userId; verdict = verdict; reason = reason
                                                 promptTokens = promptTokens; completionTokens = completionTokens; latencyMs = latencyMs
                                                 modelName = modelName; promptHash = promptHash; shadowMode = shadowMode |} ])
            return ()
        }

    /// Sets the reaction-triage NOT_SPAM cooldown for a user (event-sourced, folded into User aggregate).
    member _.RecordReactionTriageNotSpam(userId: int64, until: DateTime, actor: Actor) : Task<unit> =
        recordReactionTriageNotSpamSet userId until actor

    // -----------------------------------------------------------------------
    // Public members — Profile cache (reaction-spam triage)
    // -----------------------------------------------------------------------

    /// Returns the cached profile for this user if fetched within `maxAge` (else None).
    member _.GetCachedUserProfile(userId: int64, maxAge: TimeSpan) : Task<CachedUserProfile option> =
        task {
            use conn = new NpgsqlConnection(connString)
            //language=postgresql
            let sql = """
SELECT photo_bytes, bio, fetched_at
FROM user_profile_cache
WHERE user_id = @userId
  AND fetched_at > @cutoff
            """
            let! rows = conn.QueryAsync<CachedUserProfile>(sql, {| userId = userId; cutoff = utcNow().Subtract maxAge |})
            return rows |> Seq.tryHead
        }

    /// Upserts the profile cache row for this user.
    member _.UpsertUserProfile(userId: int64, photoBytes: byte[] option, bio: string) : Task<unit> =
        task {
            use conn = new NpgsqlConnection(connString)
            //language=postgresql
            let sql = """
INSERT INTO user_profile_cache (user_id, photo_bytes, bio, fetched_at)
VALUES (@userId, @photo, @bio, @now)
ON CONFLICT (user_id) DO UPDATE
    SET photo_bytes = EXCLUDED.photo_bytes,
        bio         = EXCLUDED.bio,
        fetched_at  = EXCLUDED.fetched_at
            """
            let! _ = conn.ExecuteAsync(sql, {| userId = userId
                                               photo = (photoBytes |> Option.toObj)
                                               bio = bio
                                               now = utcNow() |})
            return ()
        }

    // -----------------------------------------------------------------------
    // Public members — Reaction-spam triage dossier
    // -----------------------------------------------------------------------

    /// Counts MessageReceived events for this user across all chats.
    member _.GetTotalMessagesByUser(userId: int64) : Task<int> =
        task {
            use conn = new NpgsqlConnection(connString)
            //language=postgresql
            let sql = """
SELECT COUNT(*)::INT FROM event
WHERE event_type = 'MessageReceived'
  AND (data->>'userId')::BIGINT = @userId
            """
            let! count = conn.QuerySingleAsync<int>(sql, {| userId = userId |})
            return count
        }

    /// Earliest event seen for this user — None if user has no events.
    member _.GetUserFirstSeenAt(userId: int64) : Task<DateTime option> =
        task {
            use conn = new NpgsqlConnection(connString)
            //language=postgresql
            let sql = """
SELECT MIN(created_at) FROM event
WHERE stream_id = 'user:' || @userId
   OR (event_type = 'MessageReceived' AND (data->>'userId')::BIGINT = @userId)
            """
            let! result = conn.QueryAsync<Nullable<DateTime>>(sql, {| userId = userId |})
            return
                result
                |> Seq.tryHead
                |> Option.bind (fun n -> if n.HasValue then Some n.Value else None)
        }

    /// Returns (chatId, messageId) pairs for every reaction this user has placed where we
    /// recorded the message coordinates. If `chatFilter` is Some, only returns reactions in that chat.
    /// Pre-2026-05 events lacked chatId/messageId — those are silently skipped (we can't act on them anyway).
    member _.GetReactionTargetsForUser(userId: int64, chatFilter: int64 option) : Task<UserMessage array> =
        task {
            use conn = new NpgsqlConnection(connString)
            //language=postgresql
            let sql = """
SELECT DISTINCT
    (data->>'chatId')::BIGINT     AS chat_id,
    (data->>'messageId')::INT     AS message_id
FROM event
WHERE event_type = 'UserReactionRecorded'
  AND stream_id  = 'user:' || @userId
  AND data ? 'chatId'
  AND data ? 'messageId'
  AND (@chatFilter::BIGINT IS NULL OR (data->>'chatId')::BIGINT = @chatFilter)
            """
            let! rows = conn.QueryAsync<UserMessage>(sql, {| userId = userId; chatFilter = (chatFilter |> Option.toNullable) |})
            return Array.ofSeq rows
        }

    /// Returns the user's last `n` events (mix of MessageReceived and UserReactionRecorded), newest first.
    /// Old reaction events (pre-PR) without chatId surface with chat_id = 0; renderer treats that as
    /// "(unknown chat)" rather than inventing a fake one.
    member _.GetRecentDossierEvents(userId: int64, n: int) : Task<DossierEvent array> =
        task {
            use conn = new NpgsqlConnection(connString)
            //language=postgresql
            let sql = """
(
    SELECT
        (data->>'chatId')::BIGINT  AS chat_id,
        (data->>'messageId')::INT  AS message_id,
        'message'                  AS kind,
        data->>'text'              AS text,
        NULL                       AS emoji,
        created_at
    FROM event
    WHERE event_type = 'MessageReceived'
      AND (data->>'userId')::BIGINT = @userId
)
UNION ALL
(
    SELECT
        COALESCE((data->>'chatId')::BIGINT, 0::BIGINT) AS chat_id,
        COALESCE((data->>'messageId')::INT, 0::INT)   AS message_id,
        'reaction'                                    AS kind,
        NULL                                          AS text,
        data->>'emoji'                                AS emoji,
        created_at
    FROM event
    WHERE event_type = 'UserReactionRecorded'
      AND stream_id = 'user:' || @userId
)
ORDER BY created_at DESC
LIMIT @n
            """
            let! rows = conn.QueryAsync<DossierEvent>(sql, {| userId = userId; n = n |})
            return Array.ofSeq rows
        }

    // -----------------------------------------------------------------------
    // Public members — Callback operations
    // -----------------------------------------------------------------------

    /// Creates a callback by appending a CallbackCreated event.
    member _.RecordCallbackCreated(callbackId: Guid, data: CallbackMessage, targetUserId: int64, channelId: int64) : Task<unit> =
        task {
            let serializedData = serializeCallbackData data
            let! _ = EventStore.appendEvent store $"callback:{callbackId}" (fun (_: Callback) ->
                [ CallbackCreated {| data = serializedData; targetUserId = targetUserId; actionChannelId = channelId |} ])
            return ()
        }

    /// Records the action message ID after posting to channel.
    member _.RecordCallbackMessagePosted(callbackId: Guid, messageId: int) : Task<unit> =
        task {
            let! _ = EventStore.appendEvent store $"callback:{callbackId}" (fun (state: Callback) ->
                if state.IsTerminal || state.ActionMessageId.IsSome then []
                else [ CallbackMessagePosted {| actionMessageId = messageId |} ])
            return ()
        }

    /// Atomically resolves a callback (vahter clicked a button).
    /// Returns Some aggregate state if resolved, None if already terminal.
    member _.ResolveCallback(callbackId: Guid) : Task<Callback option> =
        task {
            let! (events, state) = EventStore.appendEvent store $"callback:{callbackId}" (fun (state: Callback) ->
                if state.IsTerminal || state.Data.IsNone then []
                else [ CallbackResolved ])
            return if events.IsEmpty then None else Some state
        }

    /// Expires a callback (cleanup/orphaned — no vahter action taken).
    member _.ExpireCallback(callbackId: Guid) : Task<unit> =
        expireCallbackImpl callbackId

    /// Gets all active reaction-triage callbacks for a user (BAN/SPAM/NOT_SPAM variants only).
    /// Used when one vahter resolves a reaction-spammer so the other alerts/buttons for the
    /// SAME user get cleaned up — first-click-wins, no race for other vahters to disagree.
    member _.GetActiveReactionTriageCallbacksByUserId(userId: int64) : Task<ActiveCallbackInfo array> =
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
  AND (e.data->>'data')::JSONB ->> 'Case' IN ('ReactionBan', 'ReactionSpam', 'ReactionNotSpam')
  AND NOT EXISTS (
      SELECT 1 FROM event e2
      WHERE e2.stream_id = e.stream_id
        AND e2.event_type IN ('CallbackResolved', 'CallbackExpired')
  )
                """

            let! result = conn.QueryAsync<ActiveCallbackInfo>(sql, {| userId = userId |})
            return Array.ofSeq result
        }

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

    // -----------------------------------------------------------------------
    // Snapshot rebuild (one-off backfill)
    // -----------------------------------------------------------------------

    /// Rebuilds snapshot_user / snapshot_message from the event log. Idempotent and safe to re-run:
    /// it RE-FOLDS and overwrites every row (it does not skip up-to-date rows) — required when a code
    /// change alters the fold, since stream versions are unchanged. Each message row is locked and
    /// recomputed from both its streams, so it serializes safely with concurrent live writes.
    /// Paged by stream-id prefix (keyset over idx_event_stream). `onProgress` (optional) is called at
    /// phase boundaries and every ~5000 streams. Returns the number of streams processed.
    member _.RebuildSnapshots(?batchSize: int, ?onProgress: string -> unit) : Task<int> =
        let batch = defaultArg batchSize 500
        let report = defaultArg onProgress ignore
        let reportEvery = 5000
        task {
            let mutable grandTotal = 0

            // `totalSql` returns the denominator for progress; @prefix is supplied but may be unused.
            let rebuildPrefix (prefix: string) (phase: string) (totalSql: string)
                              (handle: string -> NpgsqlConnection -> NpgsqlTransaction -> Task) =
                task {
                    use conn = new NpgsqlConnection(connString)
                    do! conn.OpenAsync()
                    let! total =
                        task {
                            try return! conn.ExecuteScalarAsync<int64>(totalSql, {| prefix = prefix |})
                            with _ -> return -1L
                        }
                    let totalStr = if total < 0L then "?" else string total
                    report $"{phase}: starting ({totalStr} streams)"
                    //language=postgresql
                    let listSql =
                        "SELECT DISTINCT stream_id FROM event WHERE stream_id LIKE @prefix AND stream_id > @cursor ORDER BY stream_id LIMIT @n"
                    let mutable cursor = ""
                    let mutable processed = 0
                    let mutable lastReported = 0
                    let mutable go = true
                    while go do
                        let! idsSeq = conn.QueryAsync<string>(listSql, {| prefix = prefix; cursor = cursor; n = batch |})
                        let ids = List.ofSeq idsSeq
                        if ids.IsEmpty then go <- false
                        else
                            let! tx = conn.BeginTransactionAsync()
                            for sid in ids do
                                do! handle sid conn tx
                                processed <- processed + 1
                            do! tx.CommitAsync()
                            do! tx.DisposeAsync()
                            cursor <- List.last ids
                            if processed - lastReported >= reportEvery then
                                report $"{phase}: {processed}/{totalStr} streams"
                                lastReported <- processed
                    report $"{phase}: done ({processed} streams)"
                    grandTotal <- grandTotal + processed
                }

            // chat:msg ids parsed from "<prefix>:{chatId}:{messageId}"
            let parseChatMsg (sid: string) =
                let parts = sid.Split(':')
                int64 parts.[1], int parts.[2]

            let countDistinctSql = "SELECT count(DISTINCT stream_id) FROM event WHERE stream_id LIKE @prefix"

            do! rebuildPrefix "user:%" "users" countDistinctSql (fun sid conn tx ->
                task {
                    let userId = sid.Substring("user:".Length) |> int64
                    let! state = store.FoldEvents((fun s e -> User.Fold(s, e)), User.Zero, sid)
                    let json = JsonSerializer.Serialize(userSnapshot { state with Id = userId }, snapshotJsonOpts)
                    do! upsertUserSnapshot userId json conn tx
                } :> Task)

            // Each message row is recomputed from BOTH streams by upsertMessageSnapshot.
            // one MessageReceived per message stream — cheaper than count(DISTINCT) via the type index.
            do! rebuildPrefix "message:%" "messages" "SELECT count(*) FROM event WHERE event_type = 'MessageReceived'" (fun sid conn tx ->
                task {
                    let chatId, messageId = parseChatMsg sid
                    do! upsertMessageSnapshot chatId messageId conn tx
                } :> Task)

            // Moderation streams whose message sibling exists were already covered above; only handle
            // orphan moderation (a bot/vahter action on a message that was never recorded).
            do! rebuildPrefix "moderation:%" "moderation (orphans)" countDistinctSql (fun sid conn tx ->
                task {
                    let chatId, messageId = parseChatMsg sid
                    let! hasMsg =
                        conn.ExecuteScalarAsync<bool>(
                            "SELECT EXISTS(SELECT 1 FROM event WHERE stream_id = @sid)",
                            {| sid = $"message:{chatId}:{messageId}" |}, tx)
                    if not hasMsg then
                        do! upsertMessageSnapshot chatId messageId conn tx
                } :> Task)

            return grandTotal
        }
