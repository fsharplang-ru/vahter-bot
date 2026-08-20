/// Pure unit coverage for `Utils.truncateTextForTg` — the 2026-08-20 fix for trace
/// 0ef9be93d5adabb29c8462b07e0e62a7: an OCR-enriched msg.Text (5405 chars) embedded unbounded
/// into a `ReportPotentialSpam` report string blew past Telegram sendMessage's 4096-char text
/// cap, and the awaited call crashed the update handler before the report ever reached the
/// vahter channel.
module VahterBanBot.Unit.Tests.TruncateTextForTgTests

open VahterBanBot.Utils
open Xunit

[<Fact>]
let ``text under the budget passes through unchanged`` () =
    let text = "short spam text"
    Assert.Equal(text, truncateTextForTg 3500 text)

[<Fact>]
let ``text exactly at the budget passes through unchanged`` () =
    let text = String.replicate 100 "x"
    Assert.Equal(text, truncateTextForTg 100 text)

[<Fact>]
let ``null text renders as empty, matching string interpolation of a null msg.Text`` () =
    Assert.Equal("", truncateTextForTg 3500 null)

[<Fact>]
let ``over-budget text is cut and carries a truncation marker with the original length`` () =
    let original = String.replicate 5405 "a"
    let result = truncateTextForTg 3500 original

    Assert.True(result.Length <= 3500)
    Assert.Contains("truncated, 5405 chars total", result)
    Assert.StartsWith(String.replicate 10 "a", result)
