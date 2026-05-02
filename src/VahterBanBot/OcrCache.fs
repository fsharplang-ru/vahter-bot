module VahterBanBot.OcrCache

open System.Threading.Tasks
open Npgsql
open Dapper

[<CLIMutable>]
type private OcrCacheRow = { extracted_text: string }

[<AllowNullLiteral>]
type IOcrCache =
    abstract member TryGetText: fileUniqueId: string -> Task<string option>
    abstract member Save: fileUniqueId: string * rawJson: string * extractedText: string -> Task<unit>

type OcrCacheRepository(connString: string) =
    interface IOcrCache with
        member _.TryGetText(fileUniqueId) = task {
            use conn = new NpgsqlConnection(connString)

            //language=postgresql
            let sql = "SELECT extracted_text FROM ocr_cache WHERE file_unique_id = @fileUniqueId"

            let! rows = conn.QueryAsync<OcrCacheRow>(sql, {| fileUniqueId = fileUniqueId |})
            // None  = no cache row (real miss).
            // Some null / Some "" = cache row exists with no/empty text — still a hit;
            //   caller treats this as "image OCRed before, no text" and skips Azure.
            return rows |> Seq.tryHead |> Option.map (fun r -> r.extracted_text)
        }

        member _.Save(fileUniqueId, rawJson, extractedText) = task {
            use conn = new NpgsqlConnection(connString)

            //language=postgresql
            let sql =
                """
INSERT INTO ocr_cache (file_unique_id, azure_response, extracted_text)
VALUES (@fileUniqueId, @rawJson::jsonb, @extractedText)
ON CONFLICT (file_unique_id) DO NOTHING
                """

            let! _ =
                conn.ExecuteAsync(
                    sql,
                    {| fileUniqueId   = fileUniqueId
                       rawJson        = rawJson
                       extractedText  = extractedText |})
            return ()
        }
