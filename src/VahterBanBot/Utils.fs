module VahterBanBot.Utils

open System
open Microsoft.FSharp.Reflection

let caseName (x: 'a) =
    let case, _ = FSharpValue.GetUnionFields(x, x.GetType())
    case.Name

let prependUsername (s: string) =
    if isNull s then
        null
    elif s.StartsWith "@" then
        s
    else "@" + s

let pluralize n s =
    if n < 2.0 then
        s
    else
        $"%.0f{n} {s}s"

let timeSpanAsHumanReadable (ts: TimeSpan) =
    let totalSeconds = ts.TotalSeconds
    if totalSeconds < 60.0 then
        pluralize totalSeconds "second"
    elif totalSeconds < 3600.0 then
        pluralize ts.TotalMinutes "minute"
    elif totalSeconds < 86400.0 then
        pluralize ts.TotalHours "hour"
    else
        pluralize ts.TotalDays "day"

/// Funogram type helpers.
module Tg =
    open Funogram.Telegram.Types

    /// Extracts the wire status string ("creator" | "administrator" | "member" |
    /// "restricted" | "left" | "kicked") from a ChatMember.
    /// NEVER branch on the ChatMember DU cases directly: Funogram's DU JSON
    /// converter misdiscriminates them (e.g. {"status":"member"} parses as Owner),
    /// so the case identity is unreliable — only the payload fields survive intact.
    let chatMemberStatus (member': ChatMember) : string =
        match member' with
        | ChatMember.Owner m -> m.Status
        | ChatMember.Administrator m -> m.Status
        | ChatMember.Member m -> m.Status
        | ChatMember.Restricted m -> m.Status
        | ChatMember.Left m -> m.Status
        | ChatMember.Banned m -> m.Status

    /// The affected user of a ChatMember, extracted case-agnostically
    /// (see chatMemberStatus for why matching on DU cases is unsafe).
    let chatMemberUser (member': ChatMember) : User =
        match member' with
        | ChatMember.Owner m -> m.User
        | ChatMember.Administrator m -> m.User
        | ChatMember.Member m -> m.User
        | ChatMember.Restricted m -> m.User
        | ChatMember.Left m -> m.User
        | ChatMember.Banned m -> m.User

