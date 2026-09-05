module VahterBanBot.LlmVerdictCache

open System
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Npgsql
open Dapper
open VahterBanBot.SpamTextCache

/// A previously-computed LLM verdict, served from cache instead of re-calling Azure.
type CachedVerdict =
    { Verdict:   string
      Reason:    string option
      ModelName: string option
      CreatedAt: DateTime }

/// MD5 of the NORMALIZED text (D5, SpamTextCache.normalize) — shared by LlmTriage.fs (builds
/// keys) and Bot.fs's correction handlers (invalidates them, D2), so both always agree.
let textHash (text: string) : string =
    MD5.HashData(Encoding.UTF8.GetBytes(normalize text)) |> Convert.ToHexString |> _.ToLower()

/// Global (cross-sender) cache key for a text — SPAM/SKIP verdicts only, see LlmTriage.fs's
/// module doc comment for the safety argument.
let globalKey (text: string) : string = sprintf "text:global:%s" (textHash text)

/// Per-sender cache key for a text — NOT_SPAM verdicts (or every verdict when the global-cache
/// flag is off).
let senderKey (senderId: int64) (text: string) : string = sprintf "text:%d:%s" senderId (textHash text)

[<CLIMutable>]
type private LlmVerdictCacheRow = { verdict: string; reason: string; model_name: string; created_at: DateTime }

[<AllowNullLiteral>]
type ILlmVerdictCache =
    /// Returns the cached verdict for this key if it was stored within `maxAge`, else None.
    abstract member TryGet: cacheKey: string * maxAge: TimeSpan -> Task<CachedVerdict option>
    /// Stores (or refreshes) the verdict for this key. Only successful verdicts should be cached.
    abstract member Save: cacheKey: string * verdict: string * reason: string option * modelName: string option -> Task<unit>
    /// Deletes the row for `cacheKey`, optionally only when it currently holds `verdict` (None =
    /// any). Idempotent — a missing/non-matching row is not an error (D2 human-correction invalidation).
    abstract member Invalidate: cacheKey: string * verdict: string option -> Task<unit>

type LlmVerdictCacheRepository(connString: string) =
    interface ILlmVerdictCache with
        member _.TryGet(cacheKey, maxAge) = task {
            use conn = new NpgsqlConnection(connString)

            //language=postgresql
            let sql =
                """
SELECT verdict, reason, model_name, created_at FROM llm_verdict_cache
WHERE cache_key = @cacheKey AND created_at > NOW() - make_interval(secs => @maxAgeSeconds)
                """

            let! rows =
                conn.QueryAsync<LlmVerdictCacheRow>(
                    sql, {| cacheKey = cacheKey; maxAgeSeconds = maxAge.TotalSeconds |})
            return
                rows
                |> Seq.tryHead
                |> Option.map (fun r ->
                    { Verdict   = r.verdict
                      Reason    = Option.ofObj r.reason
                      ModelName = Option.ofObj r.model_name
                      CreatedAt = r.created_at })
        }

        member _.Save(cacheKey, verdict, reason, modelName) = task {
            use conn = new NpgsqlConnection(connString)

            //language=postgresql
            let sql =
                """
INSERT INTO llm_verdict_cache (cache_key, verdict, reason, model_name)
VALUES (@cacheKey, @verdict, @reason, @modelName)
ON CONFLICT (cache_key) DO UPDATE
    SET verdict = EXCLUDED.verdict, reason = EXCLUDED.reason,
        model_name = EXCLUDED.model_name, created_at = NOW()
                """

            let! _ =
                conn.ExecuteAsync(
                    sql,
                    {| cacheKey  = cacheKey
                       verdict   = verdict
                       reason    = Option.toObj reason
                       modelName = Option.toObj modelName |})
            return ()
        }

        member _.Invalidate(cacheKey, verdict) = task {
            use conn = new NpgsqlConnection(connString)

            //language=postgresql
            let sql =
                """
DELETE FROM llm_verdict_cache
WHERE cache_key = @cacheKey AND (@verdict IS NULL OR verdict = @verdict)
                """

            let! _ = conn.ExecuteAsync(sql, {| cacheKey = cacheKey; verdict = Option.toObj verdict |})
            return ()
        }
