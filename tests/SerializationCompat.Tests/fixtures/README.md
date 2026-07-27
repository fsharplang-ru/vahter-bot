# Serialization-compat fixtures

All files sampled from prod `event` table on 2026-07-15 and sanitized (names, usernames,
ids, chat titles, file ids, and all free text replaced; text/caption/quote/llm_reason
replaced with same-UTF-16-length filler so entity offset/length values remain valid).
Structure, field names, entity offsets/lengths, dates, and message_id values are untouched.

## messages/ — normalized `data->'rawMessage'` from MessageReceived/MessageEdited

- `custom-emoji.json` — text message with many `custom_emoji` entities (plus a `url` entity) and `link_preview_options`.
- `photo.json` — photo message with 3-size `photo` array and `sender_tag`.
- `sender-chat.json` — channel auto-forward: `sender_chat`, `forward_origin` (type channel), from = Telegram service account, `is_automatic_forward: true`.
- `quote.json` — reply with manual `quote` (text/position/is_manual) and full `reply_to_message`.
- `external-reply.json` — reply to a message in another chat: `external_reply` (chat/photo/origin) plus non-manual `quote` with entities.
- `reply-markup.json` — bot-sent photo with `reply_markup.inline_keyboard` (url buttons with `style` field), `caption_entities`, `reply_to_message`, and `guest_bot_caller_user`.
- `edited.json` — MessageEdited event: text message with `edit_date`.
- `url-entities.json` — text with `mention` + `url` entities and populated `link_preview_options.url`.
- `document.json` — `document` + parallel `animation` (video/mp4) with `thumbnail`.
- `forward-origin.json` — forwarded channel video: `forward_origin` (type channel), `video` with `qualities` array, caption.
- `empty-raw-message.json` — the `{}` CONTENT that post-V40 backfill rows (issue #166) carry as their rawMessage (a JSON-string-wrapped empty object; since V40 there is no object-*shaped* rawMessage anymore). The live app always stores rawMessage as a JSON string.
- `plain-text.json` — simple text-only message, no entities/media/reply.

## callbacks/ — `data->>'data'` from CallbackCreated, exactly as stored (compact, ASCII-escaped)

Shell shape: `{"Case":"<name>","Fields":[<one element>]}`; the inner message is a plain
nested JSON object (not double-encoded).

- `spam.json` — Case Spam, Fields[0] = `{"message": <wire Message incl. reply_to_message>}`.
- `not-a-spam.json` — Case NotASpam, message with photo/caption/caption_entities/sender_chat/forward_origin in reply_to_message.
- `mark-as-spam.json` — Case MarkAsSpam, same `{"message": ...}` field shape as Spam.
- `reaction-ban.json` — Case ReactionBan, Fields[0] = ReactionContext: `user_id`, `chat_id`, `llm_verdict`, `llm_reason` (snake_case).
- `reaction-spam.json` — Case ReactionSpam, same ReactionContext shape.
- `reaction-not-spam.json` — Case ReactionNotSpam, same ReactionContext shape.
