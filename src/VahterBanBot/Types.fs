module VahterBanBot.Types

[<CLIMutable>]
type BotConfiguration =
    { BotToken: string
      Route: string
      SecretToken: string
      LogsChannelId: int64
      ChatsToMonitor: Set<int64>
      AllowedUsers: Set<int64>
      ShouldDeleteChannelMessages: bool }
