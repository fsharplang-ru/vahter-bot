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
      ShouldDeleteChannelMessages: bool
      IgnoreSideEffects: bool
      UseFakeApi: bool
      UsePolling: bool
      CleanupOldMessages: bool
      CleanupInterval: TimeSpan
      CleanupOldLimit: TimeSpan
      UpdateChatAdminsInterval: TimeSpan option
      UpdateChatAdmins: bool
      OcrEnabled: bool
      OcrMaxFileSizeBytes: int64
      AzureOcrEndpoint: string
      AzureOcrKey: string
      MlEnabled: bool
      MlRetrainInterval: TimeSpan option
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
      MlStopWordsInChats: Dictionary<int64, string list> }

[<CLIMutable>]
type DbUser =
    { id: int64
      username: string option
      updated_at: DateTime
      created_at: DateTime }

    static member newUser(id, ?username: string) =
        { id = id
          username = username
          updated_at = DateTime.UtcNow
          created_at = DateTime.UtcNow }

    static member newUser(user: User) =
        DbUser.newUser (id = user.Id, ?username = Option.ofObj user.Username)

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
    let banMessage (vahter: int64) (message: Message) =
        if isNull message.From || isNull message.Chat then
            failwith "Message should have a user and a chat"
        { message_id = Some message.MessageId
          message_text = message.TextOrCaption
          banned_user_id = message.From.Id
          banned_at = DateTime.UtcNow
          banned_in_chat_id = Some message.Chat.Id
          banned_in_chat_username = Option.ofObj message.Chat.Username
          banned_by = vahter }

[<CLIMutable>]
type DbMessage =
    { chat_id: int64
      message_id: int
      user_id: int64
      text: string
      raw_message: string
      created_at: DateTime }
    static member newMessage(message: Telegram.Bot.Types.Message) =
        { chat_id = message.Chat.Id
          message_id = message.MessageId
          user_id = message.From.Id
          created_at = DateTime.UtcNow
          text = message.TextOrCaption
          raw_message = JsonSerializer.Serialize(message, options = jsonOptions) }

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
    | Spam of MessageWrapper

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
