namespace SerializationCompat.Tests

open System.Text.Json
open Funogram.Telegram.Types
open Xunit

/// Pins the Funogram 3.0.5 ChatMember discriminator fix: the "status" value must
/// pick the DU case (the 3.0.4 converter chose by field-shape overlap, so
/// {"status":"member"} deserialized as ChatMember.Owner). Production code now
/// branches on DU cases (coupon MembershipService), so a regression here would
/// silently misclassify members.
type ChatMemberCompatTests() =

    static let user = """{"id":456,"is_bot":false,"first_name":"Test"}"""

    static member StatusCases: TheoryData<string, string> =
        let data = TheoryData<string, string>()
        data.Add("creator", $"""{{"status":"creator","user":{user},"is_anonymous":false}}""")
        data.Add("administrator", $"""{{"status":"administrator","user":{user},"can_be_edited":false,"is_anonymous":false,"can_manage_chat":true,"can_delete_messages":true,"can_manage_video_chats":true,"can_restrict_members":true,"can_promote_members":false,"can_change_info":true,"can_invite_users":true,"can_post_stories":false,"can_edit_stories":false,"can_delete_stories":false}}""")
        data.Add("member", $"""{{"status":"member","user":{user}}}""")
        data.Add("restricted", $"""{{"status":"restricted","user":{user},"is_member":true,"can_send_messages":false,"can_send_audios":false,"can_send_documents":false,"can_send_photos":false,"can_send_videos":false,"can_send_video_notes":false,"can_send_voice_notes":false,"can_send_polls":false,"can_send_other_messages":false,"can_add_web_page_previews":false,"can_change_info":false,"can_invite_users":false,"can_pin_messages":false,"can_manage_topics":false,"until_date":0}}""")
        data.Add("left", $"""{{"status":"left","user":{user}}}""")
        data.Add("kicked", $"""{{"status":"kicked","user":{user},"until_date":0}}""")
        data

    [<Theory; MemberData(nameof ChatMemberCompatTests.StatusCases)>]
    member _.``status discriminates the ChatMember DU case`` (status: string, json: string) =
        let cm = JsonSerializer.Deserialize<ChatMember>(json, Fixtures.funogramOptions)
        let case, parsedStatus, userId =
            match cm with
            | ChatMember.Owner m -> "creator", m.Status, m.User.Id
            | ChatMember.Administrator m -> "administrator", m.Status, m.User.Id
            | ChatMember.Member m -> "member", m.Status, m.User.Id
            | ChatMember.Restricted m -> "restricted", m.Status, m.User.Id
            | ChatMember.Left m -> "left", m.Status, m.User.Id
            | ChatMember.Banned m -> "kicked", m.Status, m.User.Id
        Assert.Equal(status, case)
        Assert.Equal(status, parsedStatus)
        Assert.Equal(456L, userId)
