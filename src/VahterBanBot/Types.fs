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
      BannedBy: int64 option
      BannedAt: DateTime option
      BanReason: string option
      UpdatedAt: DateTime
      CreatedAt: DateTime }

    static member newUser(id, ?username: string) =
        { Id = id
          Username = username
          BannedBy = None
          BannedAt = None
          BanReason = None
          UpdatedAt = DateTime.UtcNow
          CreatedAt = DateTime.UtcNow }
