module VahterBanBot.Types

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open Dapper
open Telegram.Bot.Types
open Utils

// ---------------------------------------------------------------------------
// Event sourcing — raw DB row and typed event DUs
// ---------------------------------------------------------------------------

[<CLIMutable>]
type RawEvent =
    { stream_id:      string
      stream_version: int
      event_type:     string
      data:           string   // JSONB stored as text by Dapper
      created_at:     DateTime }

type ConcurrencyConflict = ConcurrencyConflict

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
        | "KILL"     -> LlmVerdict.Kill
        | "NOT_SPAM" -> LlmVerdict.NotSpam
        | "SPAM"     -> LlmVerdict.Skip
        | _          -> LlmVerdict.Error

// ---------------------------------------------------------------------------
// Per-stream event DUs
// ---------------------------------------------------------------------------

type UserEvent =
    | UsernameChanged      of {| userId: int64; username: string option |}
    | UserBanned           of {| userId: int64; bannedBy: BannedBy option; actor: Actor option; chatId: int64 option; messageId: int option; messageText: string option |}
    | UserUnbanned         of {| userId: int64; unbannedBy: int64 option; actor: Actor option |}
    | UserReactionRecorded of {| userId: int64; delta: int |}

type User =
    { Id:            int64
      BannedByActor: Actor option
      Username:      string option
      ReactionCount: int }
    member this.IsBanned = this.BannedByActor.IsSome
    static member Zero = { Id = 0L; BannedByActor = None; Username = None; ReactionCount = 0 }
    static member Fold (state: User, event: UserEvent) : User =
        match event with
        | UsernameChanged e      -> { state with Id = e.userId; Username = e.username }
        | UserBanned e           ->
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
            { state with Id = e.userId; BannedByActor = Some actor }
        | UserUnbanned e         -> { state with Id = e.userId; BannedByActor = None }
        | UserReactionRecorded e -> { state with Id = e.userId; ReactionCount = state.ReactionCount + e.delta }

    static member fromTgUser (user: Telegram.Bot.Types.User) =
        { User.Zero with Id = user.Id; Username = Option.ofObj user.Username }

    static member fromTgMessage (msg: TgMessage) =
        { User.Zero with Id = msg.SenderId; Username = Option.ofObj msg.SenderUsername }

// ---------------------------------------------------------------------------

type SpamClassification =
    | Unknown
    | Spam
    | Ham

type MessageEvent =
    | MessageReceived    of {| chatId: int64; messageId: int; userId: int64; text: string option; rawMessage: string |}
    | MessageEdited      of {| chatId: int64; messageId: int; userId: int64; text: string option; rawMessage: string |}
    | MessageDeleted     of {| chatId: int64; messageId: int; deletedBy: int64 |}
    | MessageMarkedSpam  of {| chatId: int64; messageId: int; markedBy: int64 option |}
    | MessageMarkedHam   of {| chatId: int64; messageId: int; text: string; markedBy: int64 option |}

type Message =
    { Received:       bool
      Deleted:        bool
      Classification: SpamClassification }
    static member Zero = { Received = false; Deleted = false; Classification = Unknown }
    static member Fold (state: Message, event: MessageEvent) : Message =
        match event with
        | MessageReceived _   -> { state with Received = true }
        | MessageEdited _     -> state   // edit is recorded but doesn't change aggregate state
        | MessageDeleted _    -> { state with Deleted = true }
        | MessageMarkedSpam _ -> { state with Classification = Spam }
        | MessageMarkedHam _  -> { state with Classification = Ham }

// ---------------------------------------------------------------------------


type VahterAction =
    | PotentialKill
    | ManualBan
    | PotentialSoftSpam
    | PotentialNotSpam
    | DetectedNotSpam

type AutoDeleteReason =
    | MlSpam of {| score: float |}
    | ReactionSpam of {| reactionCount: int |}
    | InvisibleMention

/// Models how an automated spam detection should be reported to the action channels.
[<RequireQualifiedAccess>]
type SpamReport =
    /// High-confidence spam: delete the message and post to Detected Spam channel
    /// with a single "NOT A SPAM" override button for vahter review.
    | Detected of reason: AutoDeleteReason
    /// Uncertain spam: do NOT delete the message, post to Potential Spam channel
    /// with Kill / MarkAsSpam / NotSpam buttons for human triage.
    | Potential of reason: AutoDeleteReason

type ModerationEvent =
    | VahterActed      of {| vahterId: int64; actionType: VahterAction; targetUserId: int64; chatId: int64; messageId: int |}
    | BotAutoDeleted   of {| chatId: int64; messageId: int; userId: int64; reason: AutoDeleteReason |}

type Moderation =
    { VahterActedCount:    int
      BotAutoDeletedCount: int }
    static member Zero = { VahterActedCount = 0; BotAutoDeletedCount = 0 }
    static member Fold (state: Moderation, event: ModerationEvent) : Moderation =
        match event with
        | VahterActed _      -> { state with VahterActedCount = state.VahterActedCount + 1 }
        | BotAutoDeleted _   -> { state with BotAutoDeletedCount = state.BotAutoDeletedCount + 1 }

// ---------------------------------------------------------------------------

type CallbackEvent =
    | CallbackCreated       of {| data: string; targetUserId: int64; actionChannelId: int64 |}
    | CallbackMessagePosted of {| actionMessageId: int |}
    | CallbackResolved
    | CallbackExpired

type Callback =
    { Data:             string option
      TargetUserId:     int64
      ActionChannelId:  int64
      ActionMessageId:  int option
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
    | MlScoredMessage          of {| chatId: int64; messageId: int; score: float; isSpam: bool |}
    | LlmClassified            of {| chatId: int64; messageId: int; verdict: string; promptTokens: int; completionTokens: int; latencyMs: int; modelName: string option; promptHash: string option |}
    | InvisibleMentionDetected of {| chatId: int64; messageId: int; userId: int64 |}

type Detection =
    { MlScore:      float option
      LlmVerdict:   string option }
    static member Zero = { MlScore = None; LlmVerdict = None }
    static member Fold (state: Detection, event: DetectionEvent) : Detection =
        match event with
        | MlScoredMessage e          -> { state with MlScore = Some e.score }
        | LlmClassified e            -> { state with LlmVerdict = Some e.verdict }
        | InvisibleMentionDetected _ -> state

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
      DetectedSpamCleanupAge: TimeSpan
      ChatsToMonitor: Dictionary<string, int64>
      AllowedUsers: Dictionary<string, int64>
      IgnoreSideEffects: bool
      UseFakeApi: bool
      UsePolling: bool
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
      // Reaction spam detection
      ReactionSpamEnabled: bool
      ReactionSpamMinMessages: int
      ReactionSpamMaxReactions: int
      // Forward spam detection
      ForwardSpamDetectionEnabled: bool
      // Inline keyboard spam detection
      InlineKeyboardSpamDetectionEnabled: bool
      // LLM shadow triage
      LlmTriageEnabled: bool
      AzureOpenAiEndpoint: string
      AzureOpenAiKey: string
      AzureOpenAiDeployment: string
      LlmChatDescriptions: Dictionary<int64, string> }

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
type MessageWrapper= { message: Telegram.Bot.Types.Message }

// This type must be backwards compatible with the previous version
// as it is used to (de)serialize the button callback data
type CallbackMessage =
    | NotASpam of MessageWrapper
    | Spam of MessageWrapper // hard kill - delete all messages and ban user in all chats
    | MarkAsSpam of MessageWrapper  // soft spam - delete message but no ban

/// JSON serializer options for event store (de)serialization.
/// Uses internal tag, unwrapped record cases, named fields, and unwrapped options.
/// WithSkippableOptionFields ensures missing JSONB keys deserialize as None (not an error).
let eventJsonOpts =
    JsonFSharpOptions.Default()
        .WithUnionInternalTag()
        .WithUnionUnwrapRecordCases()
        .WithUnionNamedFields()
        .WithUnwrapOption()
        .WithSkippableOptionFields()
        .ToJsonSerializerOptions()

// Callback data serialization uses different JSON options (Telegram-aware)
let private callbackJsonOpts =
    let opts = JsonFSharpOptions.Default().ToJsonSerializerOptions()
    Telegram.Bot.JsonBotAPI.Configure(opts)
    opts

let serializeCallbackData (data: CallbackMessage) : string =
    JsonSerializer.Serialize(data, callbackJsonOpts)

let deserializeCallbackData (json: string) : CallbackMessage =
    JsonSerializer.Deserialize<CallbackMessage>(json, callbackJsonOpts)

/// Lightweight DTO for callback queries (projected from events).
[<CLIMutable>]
type ActiveCallbackInfo =
    { id: Guid
      action_message_id: int option
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

[<CLIMutable>]
type LlmTriageRow =
    { LlmVerdict:       string
      VahterAction:     string   // "(pending)" when vahter hasn't acted yet
      Count:            int
      TotalTokens:      int64
      AvgLatencyMs:     float }

type LlmTriageStats =
    { rows:     LlmTriageRow array
      interval: TimeSpan option }
    override this.ToString() =
        let sb = StringBuilder()
        if this.rows.Length > 0 then
            let totalCalls   = this.rows |> Array.sumBy (fun r -> r.Count)
            let totalTokens  = this.rows |> Array.sumBy (fun r -> r.TotalTokens)
            let avgLatencyMs =
                if totalCalls > 0
                then this.rows |> Array.sumBy (fun r -> r.AvgLatencyMs * float r.Count) |> fun s -> s / float totalCalls
                else 0.0

            // Agreement: LLM verdict matches vahter action
            let agreed =
                this.rows
                |> Array.sumBy (fun r ->
                    let isMatch =
                        (r.LlmVerdict = "KILL"     && (r.VahterAction = "PotentialKill" || r.VahterAction = "ManualBan")) ||
                        (r.LlmVerdict = "SPAM"     && r.VahterAction = "PotentialSoftSpam") ||
                        (r.LlmVerdict = "NOT_SPAM" && (r.VahterAction = "PotentialNotSpam" || r.VahterAction = "DetectedNotSpam"))
                    if isMatch then r.Count else 0)
            let decided = this.rows |> Array.sumBy (fun r -> if r.VahterAction = "(pending)" then 0 else r.Count)
            let agreementPct = if decided > 0 then int (float agreed / float decided * 100.0) else 0

            let intervalStr =
                match this.interval with
                | Some ts -> $"last {timeSpanAsHumanReadable ts}"
                | None    -> "all time"

            %sb.AppendLine $"\nLLM triage ({intervalStr}): {totalCalls} calls | avg {int avgLatencyMs}ms | {totalTokens} tokens"
            %sb.AppendLine $"Agreement: {agreed}/{decided} ({agreementPct}%%)"

            // group rows by LLM verdict for display
            let byVerdict =
                this.rows
                |> Array.groupBy (fun r -> r.LlmVerdict)
                |> Array.sortBy fst

            for verdict, rows in byVerdict do
                let verdictTotal = rows |> Array.sumBy (fun r -> r.Count)
                %sb.Append $"  {verdict,-10} ({verdictTotal})"
                for row in rows |> Array.sortBy (fun r -> r.VahterAction) do
                    %sb.Append $" | {row.VahterAction}: {row.Count}"
                %sb.AppendLine ""

        sb.ToString()
