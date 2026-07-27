namespace SerializationCompat.Tests

open System.Text.Json
open Xunit
open VahterBanBot.Types
open SerializationCompat.Tests.Fixtures

/// Pins the persisted CallbackMessage payload shape (event streams "callback:{guid}"
/// and in-flight inline buttons) across the Funogram migration:
///   1. real prod payloads parse with today's deserializer and land on the right case;
///   2. today's serializer round-trips them (the shape the hand-rolled Funogram-era
///      shell in PR "VahterBanBot" must reproduce byte-compatibly);
///   3. the embedded wire Message parses into Funogram types, so the new binary can
///      read payloads written by the old one.
type CallbackCompatTests() =

    static member CallbackFixtureNames : TheoryData<string> =
        let data = TheoryData<string>()
        for name, _ in callbackFixtures () do data.Add name
        data

    static member private ExpectedCase (fixtureName: string) =
        match fixtureName with
        | "spam" -> "Spam"
        | "not-a-spam" -> "NotASpam"
        | "mark-as-spam" -> "MarkAsSpam"
        | "reaction-ban" -> "ReactionBan"
        | "reaction-spam" -> "ReactionSpam"
        | "reaction-not-spam" -> "ReactionNotSpam"
        | other -> failwith $"unknown callback fixture {other}"

    static member private CaseName (cb: CallbackMessage) =
        match cb with
        | NotASpam _ -> "NotASpam"
        | Spam _ -> "Spam"
        | MarkAsSpam _ -> "MarkAsSpam"
        | ReactionBan _ -> "ReactionBan"
        | ReactionSpam _ -> "ReactionSpam"
        | ReactionNotSpam _ -> "ReactionNotSpam"

    member private _.Fixture name =
        callbackFixtures () |> Array.find (fun (n, _) -> n = name) |> snd

    [<Theory; MemberData(nameof CallbackCompatTests.CallbackFixtureNames)>]
    member this.``prod payload parses with the current deserializer into the right case`` (name: string) =
        let payload = this.Fixture name
        let cb = deserializeCallbackData payload
        Assert.Equal(CallbackCompatTests.ExpectedCase name, CallbackCompatTests.CaseName cb)

    [<Theory; MemberData(nameof CallbackCompatTests.CallbackFixtureNames)>]
    member this.``current serializer round-trips prod payloads`` (name: string) =
        let payload = this.Fixture name
        let cb = deserializeCallbackData payload
        let reserialized = serializeCallbackData cb
        let cb2 = deserializeCallbackData reserialized
        Assert.Equal(CallbackCompatTests.CaseName cb, CallbackCompatTests.CaseName cb2)
        // message-wrapper cases must keep the same message identity through the round-trip
        match cb, cb2 with
        | (NotASpam a | Spam a | MarkAsSpam a), (NotASpam b | Spam b | MarkAsSpam b) ->
            Assert.Equal(a.message.MessageId, b.message.MessageId)
            Assert.Equal(a.message.Chat.Id, b.message.Chat.Id)
        | (ReactionBan a | ReactionSpam a | ReactionNotSpam a),
          (ReactionBan b | ReactionSpam b | ReactionNotSpam b) ->
            Assert.Equal(a.userId, b.userId)
            Assert.Equal(a.chatId, b.chatId)
            Assert.Equal(a.llmVerdict, b.llmVerdict)
        | _ -> Assert.Fail "case changed during round-trip"

    [<Theory>]
    [<InlineData("spam")>]
    [<InlineData("not-a-spam")>]
    [<InlineData("mark-as-spam")>]
    member this.``embedded wire message parses into Funogram types`` (name: string) =
        let payload = this.Fixture name
        // Read the inner message straight from the shell JSON — this is exactly what the
        // Funogram-era deserializer will do with payloads written by the old binary.
        use doc = JsonDocument.Parse payload
        let fields = doc.RootElement.GetProperty "Fields"
        let messageEl = fields.[0].GetProperty "message"
        let messageJson =
            match messageEl.ValueKind with
            | JsonValueKind.String -> messageEl.GetString()
            | _ -> messageEl.GetRawText()

        let funMsg = JsonSerializer.Deserialize<Funogram.Telegram.Types.Message>(messageJson, funogramOptions)

        // agree with today's parser
        let cb = deserializeCallbackData payload
        match cb with
        | NotASpam w | Spam w | MarkAsSpam w ->
            Assert.Equal(int64 w.message.MessageId, funMsg.MessageId)
            Assert.Equal(w.message.Chat.Id, funMsg.Chat.Id)
        | _ -> Assert.Fail $"fixture {name} unexpectedly parsed to a reaction case"
