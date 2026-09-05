/// Pure unit coverage for the prompt-v2 A/B-test changes in `LlmTriage.fs`:
///   - `formatRepetitionLine` / `needsRepetitionLookup` — the "Identical message seen earlier"
///     line, which must render byte-for-byte identically to the A/B harness's
///     `format_repetition_line` (reconstruct.py) for the three cases it defines.
///   - `selectLlmRequestParams` — gpt-5-family request-parameter selection (Temperature omitted
///     + reasoning_effort sent vs the pre-gpt-5 shape), extracted so it's testable without a live
///     Azure client / real `ChatCompletionOptions`.
module VahterBanBot.Unit.Tests.LlmPromptV2Tests

open VahterBanBot
open VahterBanBot.LlmTriage
open Xunit

// ── needsRepetitionLookup / formatRepetitionLine ────────────────────────────────

[<Fact>]
let ``needsRepetitionLookup: false below 30 chars, true at and above 30`` () =
    Assert.False(needsRepetitionLookup 0)
    Assert.False(needsRepetitionLookup 29)
    Assert.True(needsRepetitionLookup 30)
    Assert.True(needsRepetitionLookup 31)

[<Fact>]
let ``short message (under 30 chars): "not checked" regardless of repetition data`` () =
    Assert.Equal(
        "Identical message seen earlier: not checked (short message)",
        formatRepetitionLine 29 None)
    // The length gate wins even if a repetition WAS looked up (shouldn't happen for a short message).
    let hit : MessageRepetition = { total = 5; distinct_other_users = 2; distinct_chats = 1 }
    Assert.Equal(
        "Identical message seen earlier: not checked (short message)",
        formatRepetitionLine 10 (Some hit))

[<Fact>]
let ``empty/media-placeholder message (length 0): "not checked"`` () =
    Assert.Equal(
        "Identical message seen earlier: not checked (short message)",
        formatRepetitionLine 0 None)

[<Fact>]
let ``long message, zero prior sightings: "never"`` () =
    let none : MessageRepetition = { total = 0; distinct_other_users = 0; distinct_chats = 0 }
    Assert.Equal("Identical message seen earlier: never", formatRepetitionLine 42 (Some none))

[<Fact>]
let ``long message, no lookup result (None): treated the same as zero — "never"`` () =
    Assert.Equal("Identical message seen earlier: never", formatRepetitionLine 42 None)

[<Fact>]
let ``long message with prior sightings: full count/other-users/chats line`` () =
    let rep : MessageRepetition = { total = 3; distinct_other_users = 2; distinct_chats = 2 }
    Assert.Equal(
        "Identical message seen earlier: 3 times in the last 30 days (2 other users, 2 chats)",
        formatRepetitionLine 42 (Some rep))

[<Fact>]
let ``long message, sightings all from the sender's own repeats (0 other users): still renders the count`` () =
    let rep : MessageRepetition = { total = 4; distinct_other_users = 0; distinct_chats = 1 }
    Assert.Equal(
        "Identical message seen earlier: 4 times in the last 30 days (0 other users, 1 chats)",
        formatRepetitionLine 42 (Some rep))

// ── selectLlmRequestParams ───────────────────────────────────────────────────────

[<Fact>]
let ``empty LLM_REASONING_EFFORT: pre-gpt-5 shape — Temperature 0, no reasoning_effort, cap 200`` () =
    let p = selectLlmRequestParams ""
    Assert.Equal(Some 0.0f, p.Temperature)
    Assert.Equal(None, p.ReasoningEffort)
    Assert.Equal(200, p.MaxOutputTokenCount)

[<Fact>]
let ``null LLM_REASONING_EFFORT: same as empty (defensive — bot_setting values are never null in practice)`` () =
    let p = selectLlmRequestParams null
    Assert.Equal(Some 0.0f, p.Temperature)
    Assert.Equal(None, p.ReasoningEffort)
    Assert.Equal(200, p.MaxOutputTokenCount)

[<Fact>]
let ``LLM_REASONING_EFFORT="none" (the A/B-tested gpt-5.6-sol value): Temperature omitted, effort passed through, cap 200`` () =
    let p = selectLlmRequestParams "none"
    Assert.Equal(None, p.Temperature)
    Assert.Equal(Some "none", p.ReasoningEffort)
    Assert.Equal(200, p.MaxOutputTokenCount)

[<Theory>]
[<InlineData("minimal")>]
[<InlineData("low")>]
[<InlineData("medium")>]
[<InlineData("high")>]
let ``any other non-empty LLM_REASONING_EFFORT value: same omit-Temperature/pass-through shape`` (effort: string) =
    let p = selectLlmRequestParams effort
    Assert.Equal(None, p.Temperature)
    Assert.Equal(Some effort, p.ReasoningEffort)
    Assert.Equal(200, p.MaxOutputTokenCount)

[<Fact>]
let ``MaxOutputTokenCount is 200 unconditionally — not gated on whether reasoning effort is set`` () =
    Assert.Equal((selectLlmRequestParams "").MaxOutputTokenCount, (selectLlmRequestParams "none").MaxOutputTokenCount)
