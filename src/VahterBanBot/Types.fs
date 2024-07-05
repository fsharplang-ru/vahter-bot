module VahterBanBot.Types

open System
open System.Collections.Generic
open System.Text
open Utils

[<CLIMutable>]
type BotConfiguration =
    { BotToken: string
      Route: string
      SecretToken: string
      LogsChannelId: int64
      ChatsToMonitor: Dictionary<string, int64>
      AllowedUsers: Dictionary<string, int64>
      ShouldDeleteChannelMessages: bool
      IgnoreSideEffects: bool
      UseFakeTgApi: bool
      UsePolling: bool
      CleanupOldMessages: bool
      CleanupInterval: TimeSpan
      CleanupOldLimit: TimeSpan }

[<CLIMutable>]
type DbUser =
    { Id: int64
      Username: string option
      Updated_At: DateTime
      Created_At: DateTime }

    static member newUser(id, ?username: string) =
        { Id = id
          Username = username
          Updated_At = DateTime.UtcNow
          Created_At = DateTime.UtcNow }

    static member newUser(user: Telegram.Bot.Types.User) =
        DbUser.newUser (id = user.Id, ?username = Option.ofObj user.Username)

[<CLIMutable>]
type DbBanned =
    { Message_Id: int option
      Message_text: string
      Banned_User_Id: int64
      Banned_At: DateTime
      Banned_In_Chat_Id: int64 option
      Banned_In_Chat_username: string option
      Banned_By: int64 }
module DbBanned =
    let banMessage (vahter: int64) (message: Telegram.Bot.Types.Message) =
        if isNull message.From || isNull message.Chat then
            failwith "Message should have a user and a chat"
        { Message_Id = Some message.MessageId
          Message_text = message.Text
          Banned_User_Id = message.From.Id
          Banned_At = DateTime.UtcNow
          Banned_In_Chat_Id = Some message.Chat.Id
          Banned_In_Chat_username = Some message.Chat.Username
          Banned_By = vahter }

[<CLIMutable>]
type DbMessage =
    { Chat_Id: int64
      Message_Id: int
      User_Id: int64
      Created_At: DateTime }
    static member newMessage(message: Telegram.Bot.Types.Message) =
        { Chat_Id = message.Chat.Id
          Message_Id = message.MessageId
          User_Id = message.From.Id
          Created_At = DateTime.UtcNow }

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
