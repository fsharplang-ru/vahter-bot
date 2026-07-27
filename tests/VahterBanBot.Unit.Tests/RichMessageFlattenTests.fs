/// Pins RichMessageText.flatten against real spam captured in the wild
/// (table-based VPN ad with tg:// deep links, no text/caption at all) and a
/// LaTeX rich message, both parsed through the exact prod webhook path
/// (FunogramJson.parseUpdate).
module VahterBanBot.Unit.Tests.RichMessageFlattenTests

open System.IO
open BotInfra
open Funogram.Telegram.Types
open VahterBanBot
open Xunit

let private fixtureUpdate name =
    let json = File.ReadAllText(Path.Combine("fixtures", name))
    (FunogramJson.parseUpdate json).Value

let private richMessageOf (update: Update) =
    update.Message.Value.RichMessage.Value

[<Fact>]
let ``table spam: all cell text is extracted`` () =
    let flattened = fixtureUpdate "table-spam-update.json" |> richMessageOf |> RichMessageText.flatten
    Assert.Contains("Лучший бесплатный ВПН!", flattened)
    Assert.Contains("Обход белых списков", flattened)
    Assert.Contains("Первые 3 дня бесплатно", flattened)
    Assert.Contains("Стабильный доступ в интернет", flattened)

[<Fact>]
let ``table spam: url payloads of link runs are extracted`` () =
    let flattened = fixtureUpdate "table-spam-update.json" |> richMessageOf |> RichMessageText.flatten
    Assert.Contains("tg://resolve?domain=Some_VPN_bot", flattened)

[<Fact>]
let ``table spam: custom emoji alternative text is extracted`` () =
    let flattened = fixtureUpdate "table-spam-update.json" |> richMessageOf |> RichMessageText.flatten
    Assert.Contains("😊", flattened)

[<Fact>]
let ``latex: block expression is extracted`` () =
    let flattened = fixtureUpdate "latex-update.json" |> richMessageOf |> RichMessageText.flatten
    Assert.Contains(@"\int_{a}^{b} x^2", flattened)

[<Fact>]
let ``rich message survives the DB rawMessage round-trip`` () =
    // rawMessage is persisted via FunogramJson.serialize and read back with
    // FunogramJson.deserialize — RichMessage must flatten identically after it.
    let msg = (fixtureUpdate "table-spam-update.json").Message.Value
    let roundTripped = FunogramJson.deserialize<Message>(FunogramJson.serialize msg)
    Assert.Equal(
        RichMessageText.flatten msg.RichMessage.Value,
        RichMessageText.flatten roundTripped.RichMessage.Value)

[<Fact>]
let ``collapsed details, list labels, quotation credit, empty cells and media captions`` () =
    let paragraph text =
        RichBlock.Paragraph { Type = "paragraph"; Text = RichText.Plain text }
    let cell text =
        { Text = text |> Option.map RichText.Plain
          IsHeader = None
          Colspan = None
          Rowspan = None
          Align = "left"
          Valign = "top" }
    let richMessage =
        { Blocks =
            [| RichBlock.Details
                 { Type = "details"
                   Summary = RichText.Plain "summary"
                   Blocks = [| paragraph "hidden spam" |]
                   // collapsed: content must be extracted anyway
                   IsOpen = None }
               RichBlock.List
                 { Type = "list"
                   Items =
                     [| { Label = "1."
                          Blocks = [| paragraph "item text" |]
                          HasCheckbox = None
                          IsChecked = None
                          Value = None
                          Type = None } |] }
               RichBlock.BlockQuotation
                 { Type = "blockquote"
                   Blocks = [| paragraph "quoted" |]
                   Credit = Some(RichText.Plain "author") }
               RichBlock.Table
                 { Type = "table"
                   Cells = [| [| cell None; cell (Some "cell") |] |]
                   IsBordered = None
                   IsStriped = None
                   Caption = Some(RichText.Plain "table caption") }
               RichBlock.Photo
                 { Type = "photo"
                   Photo = [||]
                   HasSpoiler = None
                   Caption = Some { Text = RichText.Plain "photo caption"; Credit = None } } |]
          IsRtl = None }

    let expected =
        String.concat "\n"
            [ "summary"
              "hidden spam"
              "1."
              "item text"
              "quoted"
              "author"
              "cell"
              "table caption"
              "photo caption" ]
    Assert.Equal(expected, RichMessageText.flatten richMessage)

[<Fact>]
let ``payload lands on its own line after the visible text`` () =
    let richMessage =
        { Blocks =
            [| RichBlock.Paragraph
                 { Type = "paragraph"
                   Text =
                     RichText.ArrayOf
                         [| RichText.Plain "click "
                            RichText.Url
                                { Type = "url"
                                  Text = RichText.Plain "here"
                                  Url = "https://spam.example" } |] } |]
          IsRtl = None }
    Assert.Equal("click here\nhttps://spam.example", RichMessageText.flatten richMessage)
