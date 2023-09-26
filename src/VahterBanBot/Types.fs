module VahterBanBot.Types

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
