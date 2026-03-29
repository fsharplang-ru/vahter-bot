module VahterBanBot.Types

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open Dapper
open Telegram.Bot.Types
open Utils

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
      CleanupOldMessages: bool
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
type DbUser =
    { id: int64
      username: string option
      reaction_count: int
      updated_at: DateTime
      created_at: DateTime }

    static member newUser(id, ?username: string) =
        { id = id
          username = username
          reaction_count = 0
          updated_at = DateTime.UtcNow
          created_at = DateTime.UtcNow }

    static member newUser(user: User) =
        DbUser.newUser (id = user.Id, ?username = Option.ofObj user.Username)

    static member fromTgMessage(msg: TgMessage) =
        DbUser.newUser (id = msg.SenderId, ?username = Option.ofObj msg.SenderUsername)

[<CLIMutable>]
type DbBanned =
    { message_id: int option
      message_text: string
      banned_user_id: int64
      banned_at: DateTime
      banned_in_chat_id: int64 option
      banned_in_chat_username: string option
      banned_by: int64 }
module DbBanned =
    let banMessage (vahter: int64) (msg: TgMessage) =
        if not msg.HasSender || isNull msg.Chat then
            failwith "Message should have a sender and a chat"
        { message_id = Some msg.MessageId
          message_text = msg.Text
          banned_user_id = msg.SenderId
          banned_at = DateTime.UtcNow
          banned_in_chat_id = Some msg.ChatId
          banned_in_chat_username = Option.ofObj msg.ChatUsername
          banned_by = vahter }

[<CLIMutable>]
type DbMessage =
    { chat_id: int64
      message_id: int
      user_id: int64
      text: string
      raw_message: string
      created_at: DateTime }
    static member newMessage(msg: TgMessage) =
        { chat_id = msg.ChatId
          message_id = msg.MessageId
          user_id = msg.SenderId
          created_at = DateTime.UtcNow
          text = msg.Text
          raw_message = msg.RawJson }

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
type MessageWrapper= { message: Message }

// This type must be backwards compatible with the previous version
// as it is used to (de)serialize the button callback data
type CallbackMessage =
    | NotASpam of MessageWrapper
    | Spam of MessageWrapper // hard kill - delete all messages and ban user in all chats
    | MarkAsSpam of MessageWrapper  // soft spam - delete message but no ban

[<CLIMutable>]
type DbCallback =
    { id: Guid
      data: CallbackMessage
      created_at: DateTime
      action_message_id: int option
      action_channel_id: int64 option
      target_user_id: int64 option }

type CallbackMessageTypeHandler() =
    inherit SqlMapper.TypeHandler<CallbackMessage>()
    let callBackOptions =
        let opts = JsonFSharpOptions.Default().ToJsonSerializerOptions()
        Telegram.Bot.JsonBotAPI.Configure(opts)
        opts

    override this.SetValue(parameter, value) =
        parameter.Value <- JsonSerializer.Serialize(value, options = callBackOptions)
    override this.Parse(value) =
        JsonSerializer.Deserialize<CallbackMessage>(value.ToString(), options = callBackOptions)

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
                        (r.LlmVerdict = "KILL"     && r.VahterAction = "potential_kill") ||
                        (r.LlmVerdict = "SPAM"     && r.VahterAction = "potential_soft_spam") ||
                        (r.LlmVerdict = "NOT_SPAM" && (r.VahterAction = "potential_not_spam" || r.VahterAction = "detected_not_spam"))
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
