namespace BotInfra

open System
open System.Text
open System.Text.Json

/// Shared Azure Computer Vision OCR response parsing.
module AzureOcr =
    let extractTextFromAzureResponse (json: string) =
        try
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement

            let sb = StringBuilder()

            let addLine (text: string) =
                if not (String.IsNullOrWhiteSpace text) then
                    %sb.AppendLine(text)

            let linesFromBlocks (readResult: JsonElement) =
                match readResult.TryGetProperty("blocks") with
                | true, blocks ->
                    blocks.EnumerateArray()
                    |> Seq.iter (fun block ->
                        match block.TryGetProperty("lines") with
                        | true, lines ->
                            lines.EnumerateArray()
                            |> Seq.iter (fun lineElement ->
                                match lineElement.TryGetProperty("text") with
                                | true, text -> addLine (text.GetString())
                                | _ ->
                                    match lineElement.TryGetProperty("content") with
                                    | true, content -> addLine (content.GetString())
                                    | _ -> ())
                        | _ -> ())
                | _ -> ()

            match root.TryGetProperty("readResult") with
            | true, readResult ->
                match readResult.TryGetProperty("content") with
                | true, content -> addLine (content.GetString())
                | _ -> ()

                linesFromBlocks readResult

                if sb.Length = 0 then null else sb.ToString().TrimEnd([| '\r'; '\n' |])
            | _ -> null
        with _ ->
            null
