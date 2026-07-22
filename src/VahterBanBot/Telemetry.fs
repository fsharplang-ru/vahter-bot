module VahterBanBot.Telemetry

open System.Diagnostics
open BotInfra
open Funogram.Telegram.Types

/// Shared ActivitySource for all VahterBanBot tracing.
/// Single instance ensures all spans share the same source registration in OTEL,
/// and ActivitySource.StartActivity() picks up Activity.Current as parent —
/// so fire-and-forget spans started after Task.Run will still appear as children
/// of the webhook trace via ExecutionContext propagation.
let botActivity = new ActivitySource("VahterBanBot")

/// Best-effort identity tags common to all Update variants, meant for the top
/// postUpdate span — a single-spanset Tempo query can then match user AND chat
/// without hopping between child spans. fromUserId/fromUsername = the human who
/// caused the update (message author, button-pressing vahter, reacting user).
/// Tag names match the ones inner spans already use. Absent values are skipped.
let updateIdentityTags (update: Update) : (string * obj) list =
    let ofUser (user: User option) =
        [ match user with
          | Some u ->
              yield "fromUserId", box u.Id
              match u.Username with
              | Some username -> yield "fromUsername", box username
              | None -> ()
          | None -> () ]
    let ofChat (chat: Chat) =
        [ yield "chatId", box chat.Id
          match chat.Username with
          | Some username -> yield "chatUsername", box username
          | None -> () ]
    match update.CallbackQuery, update.MessageReaction, update.EditedOrMessage with
    | Some cq, _, _ ->
        [ yield "updateType", box "callback_query"
          yield! ofUser (Some cq.From)
          match cq.Message with
          | Some (MaybeInaccessibleMessage.Message m) ->
              yield! ofChat m.Chat
              yield "messageId", box m.MessageId
          | Some (MaybeInaccessibleMessage.InaccessibleMessage m) ->
              yield! ofChat m.Chat
              yield "messageId", box m.MessageId
          | None -> () ]
    | None, Some reaction, _ ->
        [ yield "updateType", box "message_reaction"
          // User is None for anonymous (ActorChat) reactions
          yield! ofUser reaction.User
          yield! ofChat reaction.Chat
          yield "messageId", box reaction.MessageId ]
    | None, None, Some message ->
        [ yield "updateType", box (if update.EditedMessage.IsSome then "edited_message" else "message")
          // channel-sender posts have From = None
          yield! ofUser message.From
          yield! ofChat message.Chat
          yield "messageId", box message.MessageId ]
    | None, None, None ->
        [ if update.ChatMember.IsSome then yield "updateType", box "chat_member"
          elif update.MyChatMember.IsSome then yield "updateType", box "my_chat_member"
          else yield "updateType", box "unknown" ]

/// Applies updateIdentityTags to an activity. Null-safe: StartActivity returns
/// null when no listener samples the source.
let setUpdateIdentityTags (activity: Activity) (update: Update) =
    if not (isNull activity) then
        for name, value in updateIdentityTags update do
            %activity.SetTag(name, value)
