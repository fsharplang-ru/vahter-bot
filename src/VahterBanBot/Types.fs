module VahterBanBot.Types

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open Dapper
open Funogram.Telegram.Types
open Utils

[<CLIMutable>]
type BotConfiguration =
    { BotToken: string
      Route: string
      SecretToken: string
      BotUserId: int64
      BotUserName: string
      LogsChannelId: int64
      ChatsToMonitor: Dictionary<string, int64>
      AllowedUsers: Dictionary<string, int64>
      ShouldDeleteChannelMessages: bool
      IgnoreSideEffects: bool
      UseFakeTgApi: bool
      CleanupOldMessages: bool
      CleanupInterval: TimeSpan
      CleanupOldLimit: TimeSpan
      UpdateChatAdminsInterval: TimeSpan option
      UpdateChatAdmins: bool
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
        DbUser.newUser (id = user.Id, ?username = user.Username)

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
        if Option.isNone message.From then
            failwith "Message should have a user and a chat"
        { message_id = Some <| message.MessageId32
          message_text = message.TextOrCaption
          banned_user_id = message.FromId
          banned_at = DateTime.UtcNow
          banned_in_chat_id = Some message.Chat.Id
          banned_in_chat_username = message.Chat.Username
          banned_by = vahter }

[<CLIMutable>]
type DbMessage =
    { chat_id: int64
      message_id: int
      user_id: int64
      text: string
      raw_message: string
      created_at: DateTime }
    static member newMessage(message: Funogram.Telegram.Types.Message) =
        { chat_id = message.Chat.Id
          message_id = message.MessageId32
          user_id = message.FromId
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
      created_at: DateTime }

type CallbackMessageTypeHandler() =
    inherit SqlMapper.TypeHandler<CallbackMessage>()
    let callBackOptions =
        jsonOptions

    override this.SetValue(parameter, value) =
        parameter.Value <- JsonSerializer.Serialize(value, options = callBackOptions)
    override this.Parse(value) =
        JsonSerializer.Deserialize<CallbackMessage>(value.ToString(), options = callBackOptions)

[<CLIMutable>]
type UserStats =
    { good: int
      bad: int }
