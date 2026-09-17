/// Pure unit coverage for `BotInfra.Observability.redactTelegramUrl`, the helper that
/// keeps a live Telegram bot token out of the `url.full`/`http.url` span attributes.
module VahterBanBot.Unit.Tests.RedactTelegramUrlTests

open System
open BotInfra
open Xunit

[<Fact>]
let ``api call URL has the token segment redacted`` () =
    let uri = Uri("https://api.telegram.org/bot123456789:AAExampleTokenValueWouldBeHere/getFile")
    Assert.Equal("https://api.telegram.org/<redacted>/getFile", Observability.redactTelegramUrl uri)

[<Fact>]
let ``file download URL has the token segment redacted`` () =
    let uri = Uri("https://api.telegram.org/file/bot123456789:AAExampleTokenValueWouldBeHere/photos/file_1.jpg")
    Assert.Equal("https://api.telegram.org/file/<redacted>/photos/file_1.jpg", Observability.redactTelegramUrl uri)

[<Fact>]
let ``query string is dropped from the redacted URL`` () =
    let uri = Uri("https://api.telegram.org/bot123456789:AAExampleTokenValueWouldBeHere/getUpdates?offset=42")
    Assert.Equal("https://api.telegram.org/<redacted>/getUpdates", Observability.redactTelegramUrl uri)
