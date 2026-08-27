/// Postgres-backed cache seeded from manual `/ban`s: catches an exact repeat of a just-banned
/// message's text before the next scheduled ML retrain would ever see it. Measured over 103
/// days of production traffic: of 408 "addressable" messages (spam recurring after a first
/// manual ban that the model still misses), 89 arrive before any retrain could have happened.
/// A text cache catches 44 of those 89; retraining every 2 hours instead of nightly catches
/// only 2 — a lookup needs one example, a model needs enough to move a decision boundary.
///
/// Storage is the `spam_text_seed` table (V45), not a process-local dict — a single-pod
/// ConcurrentDictionary can't see cross-pod bans. Swept by the existing daily cleanup job.
///
/// Normalization is DELIBERATELY conservative: NFKC, invariant-lowercase, trim, collapse
/// internal whitespace runs to one space. That is ALL — URLs and digits are NOT stripped or
/// tokenized. An earlier attempt reused a clustering normalizer that mapped URLs -> "<url>" and
/// digits -> "<num>"; one banned message normalized down to the single character "." and the
/// cache then killed 70 innocent bare-period replies. Aggressive normalization is the known
/// failure mode here — do not "improve" this without re-reading that incident.
module VahterBanBot.SpamTextCache

open System
open System.Text
open System.Text.RegularExpressions
open System.Threading.Tasks
open Npgsql
open Dapper

/// Collapses any run of whitespace (including newlines/tabs) to a single space.
let private whitespaceRun = Regex(@"\s+", RegexOptions.Compiled)

/// Conservative cache-key normalization — see the module doc above. NFKC first (so visually/
/// semantically identical Unicode encodings collapse to the same key), then invariant-lowercase,
/// then collapse internal whitespace runs, then trim. Deliberately does NOT strip URLs, digits,
/// or punctuation.
let normalize (text: string) : string =
    if isNull text then ""
    else
        let nfkcLower = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant()
        (whitespaceRun.Replace(nfkcLower, " ")).Trim()

/// Identifies the /ban that seeded a cache hit, so a hit can be traced back to its cause
/// (chat_id, message_id) for logs/traces.
type SpamCacheHit =
    { SeedChatId: int64
      SeedMessageId: int64 }

[<CLIMutable>]
type private SpamTextSeedRow =
    { chat_id: int64
      message_id: int64 }

[<AllowNullLiteral>]
type ISpamTextCache =
    /// Seeds the cache from a manually-banned message's text. `bannedAt` is the ban's own
    /// timestamp, not necessarily "now". Returns false when normalized text is shorter than
    /// `minLength`; idempotent upsert, so re-seeding just refreshes the expiry.
    abstract member Seed: text: string * minLength: int * ttl: TimeSpan * chatId: int64 * messageId: int64 * bannedAt: DateTime -> Task<bool>
    /// Looks up a normalized-text match that has not expired as of `now`.
    abstract member TryGet: text: string * now: DateTime -> Task<SpamCacheHit option>
    /// Test-only: drops all seeds.
    abstract member Clear: unit -> Task

/// Production implementation: every Seed/TryGet is an indexed round trip to `spam_text_seed`
/// (V45 migration), shared across all pods. Registered as a DI singleton (Program.fs).
type SpamTextCache(connString: string) =
    interface ISpamTextCache with
        member _.Seed(text, minLength, ttl, chatId, messageId, bannedAt) =
            task {
                let key = normalize text
                if key.Length < minLength then
                    return false
                else
                    use conn = new NpgsqlConnection(connString)

                    //language=postgresql
                    let sql =
                        """
INSERT INTO spam_text_seed (normalized_text, chat_id, message_id, seeded_at, expires_at)
VALUES (@key, @chatId, @messageId, @bannedAt, @expiresAt)
ON CONFLICT (normalized_text) DO UPDATE
    SET chat_id = EXCLUDED.chat_id, message_id = EXCLUDED.message_id,
        seeded_at = EXCLUDED.seeded_at, expires_at = EXCLUDED.expires_at
                        """

                    let! _ =
                        conn.ExecuteAsync(
                            sql,
                            {| key = key; chatId = chatId; messageId = messageId
                               bannedAt = bannedAt; expiresAt = bannedAt.Add ttl |})
                    return true
            }

        member _.TryGet(text, now) =
            task {
                let key = normalize text
                if key.Length = 0 then
                    return None
                else
                    use conn = new NpgsqlConnection(connString)

                    //language=postgresql
                    let sql =
                        """
SELECT chat_id, message_id FROM spam_text_seed
WHERE normalized_text = @key AND expires_at > @now
                        """

                    let! rows = conn.QueryAsync<SpamTextSeedRow>(sql, {| key = key; now = now |})
                    return
                        rows
                        |> Seq.tryHead
                        |> Option.map (fun r -> { SeedChatId = r.chat_id; SeedMessageId = r.message_id })
            }

        member _.Clear() =
            task {
                use conn = new NpgsqlConnection(connString)
                let! _ = conn.ExecuteAsync("DELETE FROM spam_text_seed")
                return ()
            } :> Task
