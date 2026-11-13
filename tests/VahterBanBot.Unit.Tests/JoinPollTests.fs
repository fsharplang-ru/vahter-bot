module VahterBanBot.Unit.Tests.JoinPollTests

open VahterBanBot
open Xunit

[<Theory>]
[<InlineData(16, 16, 100, 32, 0)>]
[<InlineData(64, 64, 100, 100, 0)>]
[<InlineData(50, 50, 50, 50, 0)>]
[<InlineData(16, 8, 100, 16, 500)>]
[<InlineData(32, 3, 100, 16, 1000)>]
[<InlineData(100, 0, 100, 50, 1000)>]
[<InlineData(5, 0, 5, 5, 1000)>]
let ``Claim size and poll delay follow ready work`` limit count cap next delay =
    Assert.Equal<int * int>((next, delay), JoinDeletions.nextPoll limit count cap)
