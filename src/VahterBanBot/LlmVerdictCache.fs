module VahterBanBot.LlmVerdictCache

open System
open System.Threading.Tasks
open Npgsql
open Dapper

/// A previously-computed LLM verdict, served from cache instead of re-calling Azure.
type CachedVerdict =
    { Verdict:   string
      Reason:    string option
      ModelName: string option }

[<CLIMutable>]
type private LlmVerdictCacheRow = { verdict: string; reason: string; model_name: string }

[<AllowNullLiteral>]
type ILlmVerdictCache =
    /// Returns the cached verdict for this key if it was stored within `maxAge`, else None.
    abstract member TryGet: cacheKey: string * maxAge: TimeSpan -> Task<CachedVerdict option>
    /// Stores (or refreshes) the verdict for this key. Only successful verdicts should be cached.
    abstract member Save: cacheKey: string * verdict: string * reason: string option * modelName: string option -> Task<unit>

type LlmVerdictCacheRepository(connString: string) =
    interface ILlmVerdictCache with
        member _.TryGet(cacheKey, maxAge) = task {
            use conn = new NpgsqlConnection(connString)

            //language=postgresql
            let sql =
                """
SELECT verdict, reason, model_name FROM llm_verdict_cache
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
                      ModelName = Option.ofObj r.model_name })
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
