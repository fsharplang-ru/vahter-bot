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
type User =
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
