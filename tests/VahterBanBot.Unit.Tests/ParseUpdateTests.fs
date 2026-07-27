module VahterBanBot.Unit.Tests.ParseUpdateTests

open BotInfra
open Xunit

[<Fact>]
let ``valid update parses`` () =
    let update = FunogramJson.parseUpdate """{"update_id":42}"""
    Assert.Equal(42L, update.Value.UpdateId)

[<Fact>]
let ``json null maps to None`` () =
    Assert.True((FunogramJson.parseUpdate "null").IsNone)

[<Fact>]
let ``garbage maps to None`` () =
    Assert.True((FunogramJson.parseUpdate "{not json").IsNone)

[<Fact>]
let ``empty body maps to None`` () =
    Assert.True((FunogramJson.parseUpdate "").IsNone)
