module VahterBanBot.Tests.SnapshotTests

open System
open System.Net
open System.Text.Json
open VahterBanBot.Types
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

// Verifies the snapshot_* read models stay in lockstep with the event log:
// every write path updates the matching snapshot row in the same TX, message and
// moderation streams co-write the same snapshot_message row, and RebuildSnapshots
// reconstructs everything from the log.

// Helpers for the controlled-timeline status tests (module-level: F# class `let`s must precede members).
let private raw () = JsonSerializer.SerializeToElement("{}")
let private t (min: int) = DateTime(2026, 1, 1, 0, min, 0, DateTimeKind.Utc)
let private received chat mId user =
    MessageReceived {| chatId = chat; messageId = mId; userId = user; text = Some "x"; rawMessage = raw () |}

type SnapshotTests(fixture: MlDisabledVahterTestContainers) =

    [<Fact>]
    let ``Receiving a message writes message + user snapshots (INSERT path, service role)`` () = task {
        let sender = Tg.user(username = "snap_sender")
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], from = sender, text = "hello snapshot")
        let! _ = fixture.SendMessage msgUpdate
        let msg = msgUpdate.Message

        // snapshot_message — every message_data generated column touched
        let! snap = fixture.TryGetSnapshotMessage(msg.Chat.Id, msg.MessageId)
        Assert.True(snap.IsSome, "snapshot_message row should exist after MessageReceived")
        let s = snap.Value
        Assert.Equal("hello snapshot", s.text)
        Assert.Equal(System.Nullable msg.From.Id, s.user_id)
        Assert.Equal("Unknown", s.spam_status)          // no verdict yet
        Assert.Equal(System.Nullable false, s.deleted)
        Assert.Equal(System.Nullable 1, s.msg_version)  // single MessageReceived
        Assert.False(s.mod_version.HasValue)            // moderation stream not touched
        Assert.True(s.created_at.HasValue)              // receipt time recorded

        // snapshot_user INSERTed by the same flow (UpsertUser); non-ban generated columns touched
        let! userSnap = fixture.TryGetSnapshotUser(msg.From.Id)
        Assert.True(userSnap.IsSome, "snapshot_user row should exist after the sender is upserted")
        Assert.Equal("snap_sender", userSnap.Value.username)
        Assert.Equal(System.Nullable false, userSnap.Value.banned)
        Assert.False(userSnap.Value.banned_at.HasValue)  // not banned -> NULL timestamptz column
        Assert.True(userSnap.Value.reaction_count.HasValue)
    }

    [<Fact>]
    let ``Ban on reply writes user + moderation snapshots and keeps message_data`` () = task {
        // 1) spam message recorded -> message_data populated
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "buy crypto now")
        let! _ = fixture.SendMessage msgUpdate
        let msg = msgUpdate.Message

        // 2) vahter bans on reply -> UserBanned (user stream) + VahterActed/ManualBan (moderation stream)
        let! banResp =
            Tg.replyMsg(msg, "/ban", fixture.Vahters[0])
            |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, banResp.StatusCode)

        // user snapshot reflects the ban (UPDATE path; banned_at exercises the IMMUTABLE timestamptz column)
        let! userSnap = fixture.TryGetSnapshotUser(msg.From.Id)
        Assert.True(userSnap.IsSome, "snapshot_user row should exist for the banned user")
        Assert.Equal(System.Nullable true, userSnap.Value.banned)
        Assert.True(userSnap.Value.banned_at.HasValue, "banned_at generated column should be populated")
        Assert.Equal(System.Nullable fixture.Vahters[0].Id, userSnap.Value.banned_by)

        // message snapshot: moderation_data added on the SAME row (UPDATE), message_data intact
        let! snap = fixture.TryGetSnapshotMessage(msg.Chat.Id, msg.MessageId)
        Assert.True(snap.IsSome)
        let s = snap.Value
        Assert.Equal("buy crypto now", s.text)            // message_data not clobbered
        Assert.Equal(System.Nullable 1, s.msg_version)
        Assert.Equal("Spam", s.vahter_verdict)            // ManualBan -> Spam (moderation_data view)
        Assert.Equal("Spam", s.spam_status)               // the cross-stream message status reflects the ban
        Assert.Equal(System.Nullable false, s.bot_auto_deleted)  // vahter action, not a bot auto-delete
        Assert.True(s.mod_version.HasValue)               // moderation stream now present
    }

    [<Fact>]
    let ``RebuildSnapshots reconstructs the read models from the log and is idempotent`` () = task {
        // produce some history
        let m1 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "rebuild me one")
        let! _ = fixture.SendMessage m1
        let m2 = Tg.quickMsg(chat = fixture.ChatsToMonitor[0], text = "rebuild me two")
        let! _ = fixture.SendMessage m2

        // wipe the read models, then rebuild purely from events
        do! fixture.ClearSnapshots()
        let! gone = fixture.TryGetSnapshotMessage(m1.Message.Chat.Id, m1.Message.MessageId)
        Assert.True(gone.IsNone, "snapshot should be empty after clear")

        let! count1 = fixture.RebuildSnapshots()
        Assert.True(count1 > 0, "rebuild should process at least the streams we created")

        let! snap = fixture.TryGetSnapshotMessage(m1.Message.Chat.Id, m1.Message.MessageId)
        Assert.True(snap.IsSome, "snapshot_message should be reconstructed by rebuild")
        Assert.Equal("rebuild me one", snap.Value.text)

        // second run is a no-op in effect (idempotent): same stream count, guards prevent regress
        let! count2 = fixture.RebuildSnapshots()
        Assert.Equal(count1, count2)
        let! snap2 = fixture.TryGetSnapshotMessage(m1.Message.Chat.Id, m1.Message.MessageId)
        Assert.Equal("rebuild me one", snap2.Value.text)
    }

    // ---- spam_status folded from BOTH streams (controlled-timeline raw events + rebuild) ----
    // The pure verdict logic is covered by Message.FoldTimeline tests; these verify the snapshot
    // projection/rebuild actually fold both streams into snapshot_message.spam_status.

    [<Fact>]
    let ``spam_status is Spam when the message was bot auto-deleted (moderation stream)`` () = task {
        let chat, mId = -97001L, 1
        do! fixture.InsertRawEvent($"message:{chat}:{mId}", 1, received chat mId 5001L, t 0)
        do! fixture.InsertRawEvent($"moderation:{chat}:{mId}", 1,
                BotAutoDeleted {| chatId = chat; messageId = mId; userId = 5001L; reason = MlSpam {| score = 4.0 |} |}, t 1)
        let! _ = fixture.RebuildSnapshots()
        let! snap = fixture.TryGetSnapshotMessage(chat, mId)
        Assert.Equal("Spam", snap.Value.spam_status)
    }

    [<Fact>]
    let ``spam_status follows latest verdict across streams: bot-deleted then marked ham -> Ham`` () = task {
        let chat, mId = -97002L, 1
        do! fixture.InsertRawEvent($"message:{chat}:{mId}", 1, received chat mId 5002L, t 0)
        do! fixture.InsertRawEvent($"moderation:{chat}:{mId}", 1,
                BotAutoDeleted {| chatId = chat; messageId = mId; userId = 5002L; reason = MlSpam {| score = 4.0 |} |}, t 1)
        do! fixture.InsertRawEvent($"message:{chat}:{mId}", 2,
                MessageMarkedHam {| chatId = chat; messageId = mId; text = "x"; markedBy = Some 34L |}, t 2)
        let! _ = fixture.RebuildSnapshots()
        let! snap = fixture.TryGetSnapshotMessage(chat, mId)
        Assert.Equal("Ham", snap.Value.spam_status)
    }

    [<Fact>]
    let ``spam_status follows latest verdict across streams: marked ham then bot-deleted -> Spam`` () = task {
        let chat, mId = -97003L, 1
        do! fixture.InsertRawEvent($"message:{chat}:{mId}", 1, received chat mId 5003L, t 0)
        do! fixture.InsertRawEvent($"message:{chat}:{mId}", 2,
                MessageMarkedHam {| chatId = chat; messageId = mId; text = "x"; markedBy = Some 34L |}, t 1)
        do! fixture.InsertRawEvent($"moderation:{chat}:{mId}", 1,
                BotAutoDeleted {| chatId = chat; messageId = mId; userId = 5003L; reason = MlSpam {| score = 4.0 |} |}, t 2)
        let! _ = fixture.RebuildSnapshots()
        let! snap = fixture.TryGetSnapshotMessage(chat, mId)
        Assert.Equal("Spam", snap.Value.spam_status)
    }
