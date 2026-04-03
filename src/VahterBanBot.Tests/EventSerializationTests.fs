module VahterBanBot.Tests.EventSerializationTests

open System.Text.Json
open VahterBanBot.Types
open Xunit

/// Tests that optional fields missing from JSONB deserialize to None (not an error).
/// This validates backward compatibility: events recorded before modelName/promptHash
/// were added must still deserialize correctly.

[<Fact>]
let ``LlmClassified without modelName and promptHash deserializes both as None`` () =
    let json =
        """{"Case":"LlmClassified","chatId":100,"messageId":42,"verdict":"KILL","promptTokens":200,"completionTokens":5,"latencyMs":350}"""
    let event = JsonSerializer.Deserialize<DetectionEvent>(json, eventJsonOpts)
    match event with
    | LlmClassified e ->
        Assert.Equal(None, e.modelName)
        Assert.Equal(None, e.promptHash)
        Assert.Equal("KILL", e.verdict)
    | other -> Assert.Fail $"Expected LlmClassified but got {other}"

[<Fact>]
let ``LlmClassified with modelName and promptHash deserializes correctly`` () =
    let json =
        """{"Case":"LlmClassified","chatId":100,"messageId":42,"verdict":"NOT_SPAM","promptTokens":150,"completionTokens":3,"latencyMs":400,"modelName":"gpt-4o-mini","promptHash":"abc123def456"}"""
    let event = JsonSerializer.Deserialize<DetectionEvent>(json, eventJsonOpts)
    match event with
    | LlmClassified e ->
        Assert.Equal(Some "gpt-4o-mini", e.modelName)
        Assert.Equal(Some "abc123def456", e.promptHash)
        Assert.Equal("NOT_SPAM", e.verdict)
    | other -> Assert.Fail $"Expected LlmClassified but got {other}"

[<Fact>]
let ``LlmClassified serialization round-trip preserves optional fields`` () =
    let original =
        LlmClassified {| chatId = 100L; messageId = 42; verdict = "SPAM"
                         promptTokens = 100; completionTokens = 5; latencyMs = 300
                         modelName = Some "gpt-4o-mini"; promptHash = Some "deadbeef" |}
    let json = JsonSerializer.Serialize(original, eventJsonOpts)
    let roundtripped = JsonSerializer.Deserialize<DetectionEvent>(json, eventJsonOpts)
    match roundtripped with
    | LlmClassified e ->
        Assert.Equal(Some "gpt-4o-mini", e.modelName)
        Assert.Equal(Some "deadbeef", e.promptHash)
        Assert.Equal("SPAM", e.verdict)
    | other -> Assert.Fail $"Expected LlmClassified but got {other}"

[<Fact>]
let ``BannedByAI serialization round-trip preserves all fields`` () =
    let original =
        UserBanned {| userId = 999L
                      bannedBy = BannedByAI {| chatId = 123L; messageId = 77; messageText = Some "buy crypto"
                                               modelName = "gpt-4o-mini"; promptHash = "cafebabe" |} |}
    let json = JsonSerializer.Serialize(original, eventJsonOpts)
    let roundtripped = JsonSerializer.Deserialize<UserEvent>(json, eventJsonOpts)
    match roundtripped with
    | UserBanned e ->
        match e.bannedBy with
        | BannedByAI a ->
            Assert.Equal(123L, a.chatId)
            Assert.Equal(77, a.messageId)
            Assert.Equal(Some "buy crypto", a.messageText)
            Assert.Equal("gpt-4o-mini", a.modelName)
            Assert.Equal("cafebabe", a.promptHash)
        | other -> Assert.Fail $"Expected BannedByAI but got {other}"
    | other -> Assert.Fail $"Expected UserBanned but got {other}"
