module VahterBanBot.Types

open System
open System.Collections.Generic

[<CLIMutable>]
type BotConfiguration =
    { BotToken: string
      Route: string
      SecretToken: string
      LogsChannelId: int64
      ChatsToMonitor: Dictionary<string, int64>
      AllowedUsers: Dictionary<string, int64>
      ShouldDeleteChannelMessages: bool }

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

module DbUser =
    let banUser vahter reason (user: DbUser) = user.Ban(vahter, ?reason = reason)

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
