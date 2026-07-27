namespace SerializationCompat.Tests

open System.Text
open System.Text.Json
open Xunit
open SerializationCompat.Tests.Fixtures

/// Proves the persisted Telegram wire JSON (written by pre-migration Telegram.Bot-era
/// code, stored in event.data->'rawMessage' — as a JSON string in live rows, see issue
/// #166; these fixtures are the unwrapped message objects) stays readable forever:
///   1. old JSON parses into Funogram types (the current binary reads old prod rows);
///   2. Funogram re-serialization keeps the JSONB shape the MlData SQL reads
///      (entities[*].type, message_id, date as unix seconds).
type MessageCompatTests() =

    static member MessageFixtureNames : TheoryData<string> = messageFixtureNames ()

    member private _.EntityTypes (root: JsonElement) =
        match root.TryGetProperty "entities" with
        | true, ents -> [ for e in ents.EnumerateArray() -> e.GetProperty("type").GetString() ]
        | false, _ -> []

    [<Theory; MemberData(nameof MessageCompatTests.MessageFixtureNames)>]
    member this.``old wire JSON parses into Funogram Message`` (name: string) =
        let json = messageFixture name
        let original = JsonDocument.Parse(json).RootElement

        let msg = JsonSerializer.Deserialize<Funogram.Telegram.Types.Message>(json, funogramOptions)

        Assert.Equal(original.GetProperty("message_id").GetInt64(), msg.MessageId)
        Assert.Equal(original.GetProperty("chat").GetProperty("id").GetInt64(), msg.Chat.Id)
        match original.TryGetProperty "text" with
        | true, t -> Assert.Equal(Some (t.GetString()), msg.Text)
        | false, _ -> ()
        // entity types survive with their exact wire strings
        let expectedEntities = this.EntityTypes original
        let actualEntities =
            msg.Entities |> Option.map (Array.map (fun e -> e.Type) >> Array.toList) |> Option.defaultValue []
        Assert.Equal<string list>(expectedEntities, actualEntities)

    [<Theory; MemberData(nameof MessageCompatTests.MessageFixtureNames)>]
    member this.``Funogram round-trip preserves the JSONB shape the SQL reads`` (name: string) =
        let json = messageFixture name
        let original = JsonDocument.Parse(json).RootElement

        let msg = JsonSerializer.Deserialize<Funogram.Telegram.Types.Message>(json, funogramOptions)
        let reserialized = Encoding.UTF8.GetString(Funogram.Tools.toJson msg)
        let round = JsonDocument.Parse(reserialized).RootElement

        Assert.Equal(original.GetProperty("message_id").GetInt64(), round.GetProperty("message_id").GetInt64())
        // date must stay unix seconds, byte-identical (UnixTimestampDateTimeConverter)
        Assert.Equal(original.GetProperty("date").GetInt64(), round.GetProperty("date").GetInt64())
        Assert.Equal(original.GetProperty("chat").GetProperty("id").GetInt64(),
                     round.GetProperty("chat").GetProperty("id").GetInt64())
        // the exact predicate DB.fs MlData uses: ent->>'type' = 'custom_emoji'
        Assert.Equal<string list>(this.EntityTypes original, this.EntityTypes round)

    [<Fact>]
    member _.``custom_emoji fixture keeps satisfying the MlData SQL predicate after Funogram round-trip`` () =
        let json = messageFixture "custom-emoji"
        let msg = JsonSerializer.Deserialize<Funogram.Telegram.Types.Message>(json, funogramOptions)
        let round = JsonDocument.Parse(Funogram.Tools.toJson msg).RootElement

        let customEmojiCount =
            round.GetProperty("entities").EnumerateArray()
            |> Seq.filter (fun e -> e.GetProperty("type").GetString() = "custom_emoji")
            |> Seq.length
        Assert.True(customEmojiCount > 0, "custom_emoji entity lost in Funogram round-trip — MlData SQL would silently return 0")

    [<Fact>]
    member _.``post-V40 backfilled rawMessage content is an empty object and parsing tolerates it`` () =
        // Post-V40 backfill rows (issue #166) carry "{}" as their rawMessage CONTENT —
        // a JSON-string-wrapped empty object; there is no object *shape* anymore.
        // Nothing folds or reads fields from them; the only obligation is not to throw.
        let json = emptyRawMessageFixture ()
        let funMsg = JsonSerializer.Deserialize<Funogram.Telegram.Types.Message>(json, funogramOptions)
        Assert.Equal(0L, funMsg.MessageId)
