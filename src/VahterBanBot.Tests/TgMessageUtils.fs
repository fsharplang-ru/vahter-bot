module VahterBanBot.Tests.TgMessageUtils

open System
open Telegram.Bot.Types

type Tg() =
    static let rnd = Random.Shared
    static member user (?id: int64, ?username: string, ?firstName: string) =
        User(
            Id = (id |> Option.defaultValue (rnd.NextInt64())),
            Username = (username |> Option.defaultValue null),
            FirstName = (firstName |> Option.defaultWith (fun () -> Guid.NewGuid().ToString()))
        )
    static member chat (?id: int64, ?username: string) =
        Chat(
            Id = (id |> Option.defaultValue (rnd.NextInt64())),
            Username = (username |> Option.defaultValue null)
        )
    static member quickMsg (?text: string, ?chat: Chat, ?from: User, ?date: DateTime) =
        Update(
            Id = rnd.Next(),
            Message = 
                Message(
                    MessageId = rnd.Next(),
                    Text = (text |> Option.defaultValue (Guid.NewGuid().ToString())),
                    Chat = (chat |> Option.defaultValue (Tg.chat())),
                    From = (from |> Option.defaultValue (Tg.user())),
                    Date = (date |> Option.defaultValue DateTime.UtcNow)
                )
            )
