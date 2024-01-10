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
      UsePolling: bool }

[<CLIMutable>]
type DbUser =
    { Id: int64
      Username: string option
      Banned_By: int64 option
      Banned_At: DateTime option
      Ban_Reason: string option
      Updated_At: DateTime
      Created_At: DateTime }

    static member newUser(id, ?username: string) =
        { Id = id
          Username = username
          Banned_By = None
          Banned_At = None
          Ban_Reason = None
          Updated_At = DateTime.UtcNow
          Created_At = DateTime.UtcNow }

    static member newUser(user: Telegram.Bot.Types.User) =
        DbUser.newUser (id = user.Id, ?username = Option.ofObj user.Username)

    member this.Ban(vahter: int64, ?reason: String) =
        { this with
            Banned_By = Some vahter
            Banned_At = Some DateTime.UtcNow
            Ban_Reason = reason
            Updated_At = DateTime.UtcNow }
    member this.Unban() =
        { this with
            Banned_By = None
            Banned_At = None
            Ban_Reason = None
            Updated_At = DateTime.UtcNow }

module DbUser =
    let banUser vahter reason (user: DbUser) = user.Ban(vahter, ?reason = reason)
    let unban (user: DbUser) = user.Unban()

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
