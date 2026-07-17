module VahterBanBot.Types

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Serialization
open Dapper
open Utils
open BotInfra

// ---------------------------------------------------------------------------
// Event sourcing — typed event DUs
//
// `RawEvent` and `ConcurrencyConflict` live in `BotInfra.EventStore` and are
// pulled in via `open BotInfra` above.
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// Shared value types
// ---------------------------------------------------------------------------

// TODO: Add [<RequireQualifiedAccess>] to all DUs in the codebase for easier readability

/// Represents WHO made a decision (ban, unban, triage, etc.)
[<RequireQualifiedAccess>]
type Actor =
    | User of {| userId: int64; username: string option |}
    | Bot of {| botUserId: int64; botUsername: string |} option   // deterministic code decisions (heuristic rules, static logic)
    | ML    // ML model predictions (karma scoring, neural networks)
    | LLM of {| modelName: string; promptHash: string |}  // external LLM hosted in Azure
    member this.DisplayName =
        match this with
        | User u       -> defaultArg u.username (string u.userId)
        | Bot _        -> "Bot"
        | ML           -> "ML"
        | LLM l        -> $"LLM/{l.modelName}"

/// Legacy — will be removed after old events are migrated to Actor format.
/// Only used for deserializing old UserBanned events that were written before Actor existed.
type BannedBy =
    | BannedByVahter of {| vahterId: int64; vahterUsername: string option; chatId: int64; messageId: int; messageText: string option |}
    | BannedByAutoBan of {| chatId: int64; messageText: string option |}
    | BannedByAI of {| chatId: int64; messageId: int; messageText: string option; modelName: string; promptHash: string |}

[<RequireQualifiedAccess>]
type LlmVerdict =
    | Kill
    | NotSpam
    | Skip    // LLM "SPAM" verdict — message goes to human triage
    | Error   // HTTP failure or parse error — falls back to human triage
    static member FromString(verdictStr: string) =
        match verdictStr with
        | "SPAM"     -> LlmVerdict.Kill
        | "NOT_SPAM" -> LlmVerdict.NotSpam
        | "SKIP"     -> LlmVerdict.Skip
        | _          -> LlmVerdict.Error

/// Verdict for the reaction-spam triage pipeline (separate from LlmVerdict because the
/// reaction flow has 4 verdicts vs 3 — UNSURE explicitly routes to vahters with the
/// reason annotation, while message triage only has SPAM/SKIP/NOT_SPAM).
[<RequireQualifiedAccess>]
type LlmReactionVerdict =
    | Ban       // Strong spammer signals — total ban + delete user's messages
    | Spam      // Likely spammer in this chat — restrict reactions + delete in this chat only
    | NotSpam   // Legit lurker — set cooldown, no action
    | Unsure    // LLM is uncertain — fall through to vahter with reason annotation
    | Error     // HTTP / parse failure — same fallback as Unsure
    static member FromString(verdictStr: string) =
        match verdictStr with
        | "BAN"      -> LlmReactionVerdict.Ban
        | "SPAM"     -> LlmReactionVerdict.Spam
        | "NOT_SPAM" -> LlmReactionVerdict.NotSpam
        | "UNSURE"   -> LlmReactionVerdict.Unsure
        | _          -> LlmReactionVerdict.Error
    member this.ToWireString() =
        match this with
        | LlmReactionVerdict.Ban     -> "BAN"
        | LlmReactionVerdict.Spam    -> "SPAM"
        | LlmReactionVerdict.NotSpam -> "NOT_SPAM"
        | LlmReactionVerdict.Unsure  -> "UNSURE"
        | LlmReactionVerdict.Error   -> "ERROR"

// ---------------------------------------------------------------------------
// Per-stream event DUs
// ---------------------------------------------------------------------------

type UserEvent =
    | UsernameChanged              of {| userId: int64; username: string option |}
    | UserBanned                   of {| userId: int64; bannedBy: BannedBy option; actor: Actor option; chatId: int64 option; messageId: int64 option; messageText: string option; bannedAt: DateTime |}
    | UserUnbanned                 of {| userId: int64; unbannedBy: int64 option; actor: Actor option |}
    /// Records a user adding/removing reactions on a specific message. `chatId`, `messageId`
    /// and `emoji` are option to stay backward compatible with earlier events that didn't carry them.
    /// `emoji` is the joined emoji string of the user's NewReaction set (e.g. "🔥❤️"), useful for
    /// vahters reading the dossier — knowing which emoji was used helps spot patterns.
    | UserReactionRecorded         of {| userId: int64; chatId: int64 option; messageId: int64 option; emoji: string option; delta: int |}
    /// Reaction-spam triage verdict NOT_SPAM — sets a cooldown so a legit lurker doesn't
    /// keep re-triggering the pipeline. Set by LLM (autonomous mode) or by a vahter button.
    | ReactionTriageNotSpamSet     of {| userId: int64; until: DateTime; actor: Actor |}

type User =
    { Id:             int64
      Banned:         (Actor * DateTime) option  // (bannedBy, bannedAt)
      Username:       string option
      ReactionCount:  int
      NotSpamUntil:   DateTime option }          // reaction-spam triage cooldown
    member this.IsBanned(banExpiryDays: int, now: DateTime) =
        match this.Banned with
        | None -> false
        | Some (_, bannedAt) ->
            now - bannedAt < TimeSpan.FromDays(float banExpiryDays)
    member this.IsInReactionTriageCooldown(now: DateTime) =
        match this.NotSpamUntil with
        | Some until -> until > now
        | None -> false
    static member Zero = { Id = 0L; Banned = None; Username = None; ReactionCount = 0; NotSpamUntil = None }
    static member Fold (state: User, event: UserEvent) : User =
        match event with
        | UsernameChanged e          -> { state with Id = e.userId; Username = e.username }
        | UserBanned e               ->
            let actor =
                match e.actor with
                | Some a -> a
                | None ->
                    // backward compat: derive Actor from legacy BannedBy
                    match e.bannedBy with
                    | Some (BannedByVahter v) -> Actor.User {| userId = v.vahterId; username = v.vahterUsername |}
                    | Some (BannedByAutoBan _) -> Actor.Bot None
                    | Some (BannedByAI a) -> Actor.LLM {| modelName = a.modelName; promptHash = a.promptHash |}
                    | None -> Actor.Bot None
            { state with Id = e.userId; Banned = Some (actor, e.bannedAt) }
        | UserUnbanned e             -> { state with Id = e.userId; Banned = None }
        | UserReactionRecorded e     -> { state with Id = e.userId; ReactionCount = state.ReactionCount + e.delta }
        | ReactionTriageNotSpamSet e -> { state with Id = e.userId; NotSpamUntil = Some e.until }

    static member fromTgUser (user: Funogram.Telegram.Types.User) =
        { User.Zero with Id = user.Id; Username = user.Username }

    static member fromTgMessage (msg: TgMessage) =
        { User.Zero with Id = msg.SenderId; Username = Option.ofObj msg.SenderUsername }

// ---------------------------------------------------------------------------

type SpamClassification =
    | Unknown
    | Spam
    | Ham

type MessageEvent =
    // rawMessage is the wire-format Telegram message JSON, stored as a JSON *string*.
    // This is the single canonical shape since V40 normalized the V27 backfill rows
    // (which were '{}' objects) — see issue #166. Readers use JSONB operators with a
    // (data->>'rawMessage')::jsonb cast (see DB.MlData), never the DU field.
    | MessageReceived    of {| chatId: int64; messageId: int64; userId: int64; text: string option; rawMessage: string |}
    | MessageEdited      of {| chatId: int64; messageId: int64; userId: int64; text: string option; rawMessage: string |}
    | MessageDeleted     of {| chatId: int64; messageId: int64; deletedBy: int64 |}
    | MessageMarkedSpam  of {| chatId: int64; messageId: int64; markedBy: int64 option |}
    | MessageMarkedHam   of {| chatId: int64; messageId: int64; text: string; markedBy: int64 option |}

type Message =
    { Received:       bool
      Deleted:        bool
      Classification: SpamClassification
      Text:           string option   // latest text (received/edited/marked-ham), for the snapshot read model
      UserId:         int64 option }   // author, for the snapshot read model
    static member Zero = { Received = false; Deleted = false; Classification = Unknown; Text = None; UserId = None }
    static member Fold (state: Message, event: MessageEvent) : Message =
        match event with
        | MessageReceived e   -> { state with Received = true; Text = e.text; UserId = Some e.userId }
        | MessageEdited e     -> { state with Text = e.text; UserId = Some e.userId }   // latest text wins
        | MessageDeleted _    -> { state with Deleted = true }
        | MessageMarkedSpam _ -> { state with Classification = Spam }
        | MessageMarkedHam e  -> { state with Classification = Ham; Text = Some e.text }

// ---------------------------------------------------------------------------


type VahterAction =
    | PotentialKill
    | ManualBan
    | PotentialSoftSpam
    | PotentialNotSpam
    | DetectedNotSpam
    /// Reaction-spam triage button: BAN — total ban + remove user's reactions in all chats
    | ReactionTriageBan
    /// Reaction-spam triage button: SPAM — restrict reactions + remove existing in originating chat
    | ReactionTriageSpam
    /// Reaction-spam triage button: NOT SPAM — set cooldown, no destructive action
    | ReactionTriageNotSpam

type AutoDeleteReason =
    | MlSpam of {| score: float |}
    | ReactionSpam of {| reactionCount: int |}
    | InvisibleMention

/// Result of automated spam triage (ML + optional LLM).
[<RequireQualifiedAccess>]
type AutoVerdict =
    /// Spam detected — delete message, reduce karma, check autoban
    | Spam of score: float * actor: Actor
    /// Not spam — no action
    | NotSpam of actor: Actor
    /// Uncertain — route to human triage channel
    | Uncertain of score: float

type ModerationEvent =
    | VahterActed      of {| vahterId: int64; actionType: VahterAction; targetUserId: int64; chatId: int64; messageId: int64 |}
    | BotAutoDeleted   of {| chatId: int64; messageId: int64; userId: int64; reason: AutoDeleteReason |}

type Moderation =
    { VahterActedCount:    int
      BotAutoDeletedCount: int
      LastVahterAction:    VahterAction option }   // last action wins; drives the snapshot verdict
    static member Zero = { VahterActedCount = 0; BotAutoDeletedCount = 0; LastVahterAction = None }
    static member Fold (state: Moderation, event: ModerationEvent) : Moderation =
        match event with
        | VahterActed e      -> { state with VahterActedCount = state.VahterActedCount + 1; LastVahterAction = Some e.actionType }
        | BotAutoDeleted _   -> { state with BotAutoDeletedCount = state.BotAutoDeletedCount + 1 }

/// A message's spam/ham status spans two streams: its own `message:*` marks AND the sibling
/// `moderation:*` bot/vahter actions. This unifies them so a single fold over the time-ordered
/// merge of both streams yields the message's status — the F# home of MlData's verdict logic.
type MessageTimelineEvent =
    | FromMessage    of MessageEvent
    | FromModeration of ModerationEvent

type Message with
    /// Folds the full Message state from the merged, time-ordered timeline of BOTH streams.
    /// Spam/ham mapping mirrors DbService.MlData (last decisive event wins): BotAutoDeleted and
    /// VahterActed PotentialKill/ManualBan -> Spam; VahterActed PotentialNotSpam/DetectedNotSpam ->
    /// Ham; soft-spam and reaction-triage actions are non-decisive (status unchanged), as in MlData.
    /// The caller must feed events in (created_at, id) order.
    static member FoldTimeline (state: Message, ev: MessageTimelineEvent) : Message =
        match ev with
        | FromMessage me                     -> Message.Fold(state, me)   // Received/Text/UserId/Deleted + mark verdicts
        | FromModeration (BotAutoDeleted _)  -> { state with Classification = Spam }
        | FromModeration (VahterActed e) ->
            match e.actionType with
            | PotentialKill | ManualBan          -> { state with Classification = Spam }
            | PotentialNotSpam | DetectedNotSpam -> { state with Classification = Ham }
            | PotentialSoftSpam
            | ReactionTriageBan | ReactionTriageSpam | ReactionTriageNotSpam -> state

// ---------------------------------------------------------------------------

type CallbackEvent =
    | CallbackCreated       of {| data: string; targetUserId: int64; actionChannelId: int64 |}
    | CallbackMessagePosted of {| actionMessageId: int64 |}
    | CallbackResolved
    | CallbackExpired

type Callback =
    { Data:             string option
      TargetUserId:     int64
      ActionChannelId:  int64
      ActionMessageId:  int64 option
      IsTerminal:       bool }
    static member Zero = { Data = None; TargetUserId = 0L; ActionChannelId = 0L; ActionMessageId = None; IsTerminal = false }
    static member Fold (state: Callback, event: CallbackEvent) : Callback =
        match event with
        | CallbackCreated e       -> { state with Data = Some e.data; TargetUserId = e.targetUserId; ActionChannelId = e.actionChannelId }
        | CallbackMessagePosted e -> { state with ActionMessageId = Some e.actionMessageId }
        | CallbackResolved
        | CallbackExpired         -> { state with IsTerminal = true }

// ---------------------------------------------------------------------------

type DetectionEvent =
    | MlScoredMessage              of {| chatId: int64; messageId: int64; score: float; isSpam: bool |}
    | LlmClassified                of {| chatId: int64; messageId: int64; verdict: string; promptTokens: int; completionTokens: int; latencyMs: int; modelName: string option; promptHash: string option |}
    | InvisibleMentionDetected     of {| chatId: int64; messageId: int64; userId: int64 |}
    /// Verdict from the reaction-spam triage classifier (vision LLM evaluating profile photo + bio + history).
    /// Recorded in BOTH shadow mode (ignored for action) AND autonomous mode (load-bearing). The presence
    /// of this event for a given (userId, chatId) means a reaction-spam threshold tripped.
    | LlmReactionTriageClassified  of {| chatId: int64; userId: int64; verdict: string; reason: string option; promptTokens: int; completionTokens: int; latencyMs: int; modelName: string option; promptHash: string option; shadowMode: bool |}

type Detection =
    { MlScore:                  float option
      LlmVerdict:               string option
      LlmReactionTriageVerdict: string option }
    static member Zero = { MlScore = None; LlmVerdict = None; LlmReactionTriageVerdict = None }
    static member Fold (state: Detection, event: DetectionEvent) : Detection =
        match event with
        | MlScoredMessage e             -> { state with MlScore = Some e.score }
        | LlmClassified e               -> { state with LlmVerdict = Some e.verdict }
        | InvisibleMentionDetected _    -> state
        | LlmReactionTriageClassified e -> { state with LlmReactionTriageVerdict = Some e.verdict }

[<CLIMutable>]
type BotConfiguration =
    { BotToken: string
      Route: string
      SecretToken: string
      BotUserId: int64
      BotUserName: string
      // Channels for vahter actions
      PotentialSpamChannelId: int64
      DetectedSpamChannelId: int64
      AllLogsChannelId: int64
      /// Private vahter channel where privileged /vahter admin commands are accepted.
      /// 0 = disabled (admin commands never match).
      AdminChannelId: int64
      DetectedSpamCleanupAge: TimeSpan
      ChatsToMonitor: Dictionary<string, int64>
      AllowedUsers: Dictionary<string, int64>
      IgnoreSideEffects: bool
      UsePolling: bool
      TelegramApiBaseUrl: string
      CleanupInterval: TimeSpan
      CleanupCheckInterval: TimeSpan
      CleanupScheduledHour: int
      StatsScheduledHour: int
      CleanupOldLimit: TimeSpan
      UpdateChatAdminsInterval: TimeSpan option
      UpdateChatAdmins: bool
      OcrEnabled: bool
      OcrMaxFileSizeBytes: int64
      AzureOcrEndpoint: string
      AzureOcrKey: string
      MlEnabled: bool
      MlRetrainScheduledTime: TimeSpan
      /// When false, the scheduled daily ML retrain is skipped (the loaded model stays put).
      /// Default true in production; disabled in tests so a wall-clock-triggered retrain can't
      /// rebuild the pinned fixture model mid-suite.
      MlRetrainScheduledEnabled: bool
      MlSeed: Nullable<int>
      MlSpamDeletionEnabled: bool
      MlSpamAutobanEnabled: bool
      MlSpamAutobanCheckLastMsgCount: int
      MlSpamAutobanScoreThreshold: double
      MlTrainRandomSortData: bool
      MlTrainInterval: TimeSpan
      MlTrainCriticalMsgCount: int
      MlTrainingSetFraction: float
      MlSpamThreshold: single
      MlWarningThreshold: single
      MlMaxNumberOfIterations: int
      MlCustomEmojiThreshold: int
      MlStopWordsInChats: Dictionary<int64, string list>
      /// Time-decay weight parameter: w(t) = exp(-k * age_in_days). 0 = no decay (all weights 1.0).
      MlWeightDecayK: float
      /// Users with >= this many unique messages are immune from ML/LLM triage.
      MlOldUserMsgCount: int
      // Reaction spam detection
      ReactionSpamEnabled: bool
      ReactionSpamMinMessages: int
      ReactionSpamMaxReactions: int
      // Forward spam detection
      ForwardSpamDetectionEnabled: bool
      // Inline keyboard spam detection
      InlineKeyboardSpamDetectionEnabled: bool
      // Rich message (Bot API 10.1) spam detection
      RichMessageSpamDetectionEnabled: bool
      // LLM shadow triage
      LlmTriageEnabled: bool
      AzureOpenAiEndpoint: string
      AzureOpenAiKey: string
      AzureOpenAiDeployment: string
      LlmChatDescriptions: Dictionary<int64, string>
      /// Minutes a cached LLM verdict (text or reaction triage) is reused before re-asking
      /// the model. Dedups identical spam across channels and absorbs rate-limit bursts.
      LlmVerdictCacheTtlMinutes: int
      // Reaction-spam triage (vision LLM)
      /// When true, LLM verdict acts autonomously (UNSURE falls through to vahter).
      /// When false (default — shadow mode), LLM runs but verdict is recorded only; vahter always decides.
      LlmReactionTriageAutoAct: bool
      /// Escape hatch: if true, skip the LLM call entirely (do not even shadow). Normally false.
      LlmReactionTriageShadowDisable: bool
      /// Days after vahter/LLM verdict NOT_SPAM before re-triaging the same user.
      ReactionNotSpamCooldownDays: int
      /// Debounce window for reaction-triage per user: a reaction burst trips the threshold on every
      /// reaction, but we run triage at most once per this window. Default: 5s.
      ReactionTriageDebounce: TimeSpan
      /// Bans older than this many days are considered expired. Default: 7.
      BanExpiryDays: int
      // Ephemeral commands & confirmations (Bot API 10.2)
      /// When true, public commands (/ban, /unban, /sban) are registered with is_ephemeral,
      /// so clients send them invisibly to other chat members. Re-registration happens on restart.
      EphemeralCommandsEnabled: bool
      /// When true, the issuing vahter gets a short self-dismissing ephemeral confirmation
      /// in the chat after /ban, /sban, /unban. Visible only to the issuer.
      EphemeralConfirmationEnabled: bool }
    member this.BotActor =
        Actor.Bot (Some {| botUserId = this.BotUserId; botUsername = this.BotUserName |})

[<CLIMutable>]
type VahterStat =
    { Vahter: string
      KillCountTotal: int
      KillCountInterval: int }

type VahterStats =
    { stats: VahterStat array
      interval: TimeSpan option }
    override this.ToString() =
        let sb = StringBuilder()
        if this.stats.Length > 0 then
            if this.interval.IsSome then
                let intervalKills =
                    this.stats
                    |> Array.filter (fun x -> x.KillCountInterval > 0)
                    
                if intervalKills.Length > 0 then
                    %sb.AppendLine $"Vahter stats for the last {timeSpanAsHumanReadable this.interval.Value}:"
                    
                    intervalKills
                    |> Array.sortByDescending (fun x -> x.KillCountInterval)
                    |> Array.iteri (fun i stat ->
                        %sb.AppendLine $"%d{i+1} {prependUsername stat.Vahter} - {stat.KillCountInterval}")
                else
                    %sb.AppendLine $"No one was killed in the last {timeSpanAsHumanReadable this.interval.Value}"
                
            %sb.AppendLine "Vahter stats all time:"
            this.stats
            |> Array.sortByDescending (fun x -> x.KillCountTotal)
            |> Array.iteri (fun i stat ->
                %sb.AppendLine $"%d{i+1} {prependUsername stat.Vahter} - {stat.KillCountTotal}")
        sb.ToString()

// used as aux type to possibly extend in future without breaking changes
type MessageWrapper= { message: Funogram.Telegram.Types.Message }

/// Carries everything a reaction-spam callback handler needs. There is no "message
/// authored by the suspect" in this flow (the suspect *reacted* to someone else's
/// message), so we cannot piggy-back on MessageWrapper.
type ReactionContext =
    { userId:        int64
      chatId:        int64
      llmVerdict:    string option   // e.g. "SPAM"/"BAN"/etc — what the LLM said in shadow mode
      llmReason:     string option }

// This type must be backwards compatible with the previous version
// as it is used to (de)serialize the button callback data
type CallbackMessage =
    | NotASpam of MessageWrapper
    | Spam of MessageWrapper // hard kill - delete all messages and ban user in all chats
    | MarkAsSpam of MessageWrapper  // soft spam - delete message but no ban
    | ReactionBan of ReactionContext      // total ban + remove user's reactions in all chats + delete user's messages
    | ReactionSpam of ReactionContext     // restrict reactions + remove existing in originating chat
    | ReactionNotSpam of ReactionContext  // set cooldown, no destructive action

/// JSON serializer options for event store (de)serialization.
/// Uses internal tag, unwrapped record cases, named fields, and unwrapped options.
/// WithSkippableOptionFields ensures missing JSONB keys deserialize as None (not an error).
let eventJsonOpts =
    JsonFSharpOptions.Default()
        .WithUnionInternalTag()
        .WithUnionUnwrapRecordCases()
        .WithUnionNamedFields()
        .WithUnwrapOption()
        .WithSkippableOptionFields(SkippableOptionFields.Always, deserializeNullAsNone = true)
        .ToJsonSerializerOptions()

/// JSON options for the snapshot read-model tables (snapshot_user / snapshot_message).
/// No union tag is needed (the DTOs are flat primitive records); options are unwrapped and
/// None fields skipped, so an absent key yields a NULL in the GENERATED column.
let snapshotJsonOpts =
    JsonFSharpOptions.Default()
        .WithUnwrapOption()
        .WithSkippableOptionFields(SkippableOptionFields.Always)
        .ToJsonSerializerOptions()

/// Flat snapshot DTOs. Keys MUST match the GENERATED-column expressions in V38__snapshot.sql.

let userSnapshot (s: User) =                 // -> snapshot_user.state
    let bannedByUserId =
        s.Banned |> Option.bind (fun (a, _) -> match a with Actor.User u -> Some u.userId | _ -> None)
    {| userId         = s.Id
       username       = s.Username
       banned         = s.Banned.IsSome
       bannedAt       = s.Banned |> Option.map snd
       bannedByUserId = bannedByUserId
       reactionCount  = s.ReactionCount
       notSpamUntil   = s.NotSpamUntil |}

let messageSnapshot (s: Message) =           // -> snapshot_message.message_data
    {| userId         = s.UserId
       text           = s.Text
       classification = (match s.Classification with SpamClassification.Spam -> "Spam" | SpamClassification.Ham -> "Ham" | SpamClassification.Unknown -> "Unknown")
       received       = s.Received
       deleted        = s.Deleted |}

let moderationSnapshot (s: Moderation) =     // -> snapshot_message.moderation_data
    let verdict =
        match s.LastVahterAction with
        | Some (PotentialKill | ManualBan | ReactionTriageBan | ReactionTriageSpam) -> Some "Spam"
        | Some (PotentialNotSpam | DetectedNotSpam | ReactionTriageNotSpam)         -> Some "NotSpam"
        | Some PotentialSoftSpam | None                                            -> None
    {| botAutoDeleted      = s.BotAutoDeletedCount > 0
       verdict             = verdict
       vahterActedCount    = s.VahterActedCount
       botAutoDeletedCount = s.BotAutoDeletedCount |}

// ---------------------------------------------------------------------------
// CallbackMessage wire shell — hand-rolled JSON (de)serialization.
//
// Must stay byte-compatible with the payloads the pre-Funogram binary wrote via
// FSharp.SystemTextJson + Telegram.Bot converters (persisted in callback:{guid}
// event streams and referenced by in-flight inline buttons):
//   {"Case":"<CaseName>","Fields":[<one payload object>]}
// MessageWrapper cases embed the Telegram message as a PLAIN NESTED OBJECT in
// wire format (snake_case); ReactionContext cases embed a snake_case object with
// user_id/chat_id and optional llm_verdict/llm_reason (omitted when None, missing
// or null reads as None). Pinned by tests/SerializationCompat.Tests/CallbackCompatTests.fs.
// ---------------------------------------------------------------------------

let private serializeMessageWrapper (w: MessageWrapper) : JsonNode =
    let payload = JsonObject()
    payload["message"] <- JsonNode.Parse(FunogramJson.serialize w.message)
    payload

let private serializeReactionContext (ctx: ReactionContext) : JsonNode =
    let payload = JsonObject()
    payload["user_id"] <- JsonValue.Create ctx.userId
    payload["chat_id"] <- JsonValue.Create ctx.chatId
    match ctx.llmVerdict with
    | Some v -> payload["llm_verdict"] <- JsonValue.Create v
    | None -> ()
    match ctx.llmReason with
    | Some r -> payload["llm_reason"] <- JsonValue.Create r
    | None -> ()
    payload

let serializeCallbackData (data: CallbackMessage) : string =
    let case, payload =
        match data with
        | NotASpam w        -> "NotASpam",        serializeMessageWrapper w
        | Spam w            -> "Spam",            serializeMessageWrapper w
        | MarkAsSpam w      -> "MarkAsSpam",      serializeMessageWrapper w
        | ReactionBan ctx     -> "ReactionBan",     serializeReactionContext ctx
        | ReactionSpam ctx    -> "ReactionSpam",    serializeReactionContext ctx
        | ReactionNotSpam ctx -> "ReactionNotSpam", serializeReactionContext ctx
    let root = JsonObject()
    root["Case"] <- JsonValue.Create case
    root["Fields"] <- JsonArray(payload)
    root.ToJsonString()

let deserializeCallbackData (json: string) : CallbackMessage =
    use doc = JsonDocument.Parse json
    let root = doc.RootElement
    let case = root.GetProperty("Case").GetString()
    let field = root.GetProperty("Fields").[0]

    let readMessageWrapper () =
        let messageEl = field.GetProperty "message"
        let messageJson =
            match messageEl.ValueKind with
            // defensive: tolerate a string-encoded (double-serialized) message
            | JsonValueKind.String -> messageEl.GetString()
            | _ -> messageEl.GetRawText()
        { message = FunogramJson.deserialize<Funogram.Telegram.Types.Message> messageJson }

    let readReactionContext () =
        let optionalString (name: string) =
            match field.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.String -> Some (v.GetString())
            | _ -> None
        { userId     = field.GetProperty("user_id").GetInt64()
          chatId     = field.GetProperty("chat_id").GetInt64()
          llmVerdict = optionalString "llm_verdict"
          llmReason  = optionalString "llm_reason" }

    match case with
    | "NotASpam"        -> NotASpam (readMessageWrapper())
    | "Spam"            -> Spam (readMessageWrapper())
    | "MarkAsSpam"      -> MarkAsSpam (readMessageWrapper())
    | "ReactionBan"     -> ReactionBan (readReactionContext())
    | "ReactionSpam"    -> ReactionSpam (readReactionContext())
    | "ReactionNotSpam" -> ReactionNotSpam (readReactionContext())
    | other             -> failwith $"Unknown CallbackMessage case: {other}"

/// Lightweight DTO for callback queries (projected from events).
[<CLIMutable>]
type ActiveCallbackInfo =
    { id: Guid
      action_message_id: int64 option
      action_channel_id: int64 }

[<CLIMutable>]
type UserStats =
    { good: int
      bad: int }

[<CLIMutable>]
type VahterActionStat =
    { Vahter: string
      KillsTotal: int
      KillsInterval: int
      NotSpamTotal: int
      NotSpamInterval: int }

type VahterActionStats =
    { stats: VahterActionStat array
      interval: TimeSpan option }
    override this.ToString() =
        let sb = StringBuilder()
        if this.stats.Length > 0 then
            if this.interval.IsSome then
                let intervalActions =
                    this.stats
                    |> Array.filter (fun x -> x.KillsInterval + x.NotSpamInterval > 0)
                    
                if intervalActions.Length > 0 then
                    %sb.AppendLine $"Vahter actions for the last {timeSpanAsHumanReadable this.interval.Value}:"
                    
                    intervalActions
                    |> Array.sortByDescending (fun x -> x.KillsInterval + x.NotSpamInterval)
                    |> Array.iteri (fun i stat ->
                        let total = stat.KillsInterval + stat.NotSpamInterval
                        let medal = match i with 0 -> "🏆" | 1 -> "🥈" | 2 -> "🥉" | _ -> $"%d{i+1}."
                        %sb.AppendLine $"  {medal} {prependUsername stat.Vahter} - {total} (🚫 {stat.KillsInterval} | ✅ {stat.NotSpamInterval})")
                    
                    let totalActions = intervalActions |> Array.sumBy (fun x -> x.KillsInterval + x.NotSpamInterval)
                    %sb.AppendLine ""
                    %sb.AppendLine $"Legend: 🚫 kills | ✅ not_spam"
                    %sb.AppendLine $"Total actions: {totalActions}"
                else
                    %sb.AppendLine $"No actions in the last {timeSpanAsHumanReadable this.interval.Value}"
                
            %sb.AppendLine ""
            %sb.AppendLine "Vahter actions all time:"
            this.stats
            |> Array.sortByDescending (fun x -> x.KillsTotal + x.NotSpamTotal)
            |> Array.iteri (fun i stat ->
                let total = stat.KillsTotal + stat.NotSpamTotal
                %sb.AppendLine $"  %d{i+1}. {prependUsername stat.Vahter} - {total}")
        sb.ToString()

