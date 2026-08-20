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

/// Telegram's sendMessage caps text at 4096 chars — callers embedding unbounded user text
/// (e.g. OCR-enriched msg.Text) must pre-truncate with a budget that leaves headroom for the
/// rest of the message (header, ref token) on top of `text`.
let truncateTextForTg (budget: int) (text: string) =
    if isNull text then
        ""
    elif text.Length <= budget then
        text
    else
        let marker = $"… [truncated, {text.Length} chars total]"
        text.Substring(0, budget - marker.Length) + marker

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

    /// The affected user of a ChatMember — every case carries one.
    let chatMemberUser (member': ChatMember) : User =
        match member' with
        | ChatMember.Owner m -> m.User
        | ChatMember.Administrator m -> m.User
        | ChatMember.Member m -> m.User
        | ChatMember.Restricted m -> m.User
        | ChatMember.Left m -> m.User
        | ChatMember.Banned m -> m.User

