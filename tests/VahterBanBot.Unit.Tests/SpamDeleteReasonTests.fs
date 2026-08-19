/// Pure unit coverage for `Bot.spamDeleteReason` — the 2026-08-18 deletion-reason attribution
/// fix. Before this fix, `AutoVerdict.Spam`'s enforcement site always recorded `AutoDeleteReason.
/// MlSpam`, even when `Actor.LLM` (i.e. `LlmVerdict.Kill`) made the actual kill call — see the
/// @AvaloniaRU msg 217142 incident, where a kitten-sticker false positive was logged as a plain
/// ML-threshold verdict when the LLM had in fact decided.
module VahterBanBot.Unit.Tests.SpamDeleteReasonTests

open VahterBanBot.Bot
open VahterBanBot.Types
open Xunit

[<Fact>]
let ``LLM kill verdict (Actor.LLM) attributes to LlmSpam, carrying score and modelName`` () =
    let actor = Actor.LLM {| modelName = "gpt-4o-mini"; promptHash = "abc123" |}
    match spamDeleteReason 0.31478 actor with
    | AutoDeleteReason.LlmSpam r ->
        Assert.Equal(0.31478, r.score)
        Assert.Equal("gpt-4o-mini", r.modelName)
    | other -> Assert.Fail $"Expected LlmSpam but got {other}"

[<Fact>]
let ``plain ML-threshold verdict (Actor.ML) attributes to MlSpam`` () =
    match spamDeleteReason 0.87 Actor.ML with
    | AutoDeleteReason.MlSpam r -> Assert.Equal(0.87, r.score)
    | other -> Assert.Fail $"Expected MlSpam but got {other}"

[<Fact>]
let ``any non-LLM actor (defensive: AutoVerdict.Spam never actually carries these) falls back to MlSpam`` () =
    match spamDeleteReason 0.6 (Actor.Bot None) with
    | AutoDeleteReason.MlSpam r -> Assert.Equal(0.6, r.score)
    | other -> Assert.Fail $"Expected MlSpam but got {other}"
