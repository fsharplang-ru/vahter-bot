/// In-process cache seeded from manual `/ban`s: catches an exact repeat of a just-banned
/// message's text before the next scheduled ML retrain would ever see it. Measured over 103
/// days of production traffic: of 408 "addressable" messages (spam recurring after a first
/// manual ban that the model still misses), 89 arrive before any retrain could have happened.
/// A text cache catches 44 of those 89; retraining every 2 hours instead of nightly catches
/// only 2 — a lookup needs one example, a model needs enough to move a decision boundary.
///
/// Storage is a single in-process `ConcurrentDictionary` — no new table, no Flyway migration.
/// The event log already holds every banned message's text, so `DB.fs`'s
/// `GetRecentManualBansWithText` rehydrates the last TTL window's worth of seeds on startup
/// (see Program.fs) so a deploy/restart doesn't blank the cache.
///
/// Normalization is DELIBERATELY conservative: NFKC, invariant-lowercase, trim, collapse
/// internal whitespace runs to one space. That is ALL — URLs and digits are NOT stripped or
/// tokenized. An earlier attempt reused a clustering normalizer that mapped URLs -> "<url>" and
/// digits -> "<num>"; one banned message normalized down to the single character "." and the
/// cache then killed 70 innocent bare-period replies. Aggressive normalization is the known
/// failure mode here — do not "improve" this without re-reading that incident (see the PR
/// description for the full writeup).
module VahterBanBot.SpamTextCache

open System
open System.Collections.Concurrent
open System.Text
open System.Text.RegularExpressions

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
/// (chat_id, message_id) for logs/traces — design requirement, not decoration.
type SpamCacheHit =
    { SeedChatId: int64
      SeedMessageId: int64 }

type private Entry =
    { ExpiresAt: DateTime
      SeedChatId: int64
      SeedMessageId: int64 }

[<AllowNullLiteral>]
type ISpamTextCache =
    /// Seeds the cache from a manually-banned message's text. `bannedAt` is the ban's own
    /// timestamp, not necessarily "now" — startup rehydration passes the original event time,
    /// so a rehydrated seed expires at the SAME wall-clock instant it would have if the process
    /// had never restarted (flat TTL from the ban, never extended by a restart). Returns false
    /// (and does not store anything) when the normalized text is shorter than `minLength` —
    /// see the PR description for why the seed-hygiene floor is 40 chars, not 80.
    abstract member Seed: text: string * minLength: int * ttl: TimeSpan * chatId: int64 * messageId: int64 * bannedAt: DateTime -> bool
    /// Looks up a normalized-text match that has not expired as of `now`.
    abstract member TryGet: text: string * now: DateTime -> SpamCacheHit option
    /// Test-only: drops all seeds. This cache has no backing table to truncate between tests
    /// (unlike ILlmVerdictCache/IOcrCache) — callers that need isolation from a long-lived
    /// shared test container use this instead.
    abstract member Clear: unit -> unit

/// Production implementation: a single process-wide `ConcurrentDictionary` keyed by the
/// normalized text, expiry-checked on read. Registered as a DI singleton (Program.fs).
type SpamTextCache() =
    let store = ConcurrentDictionary<string, Entry>()
    interface ISpamTextCache with
        member _.Seed(text, minLength, ttl, chatId, messageId, bannedAt) =
            let key = normalize text
            if key.Length < minLength then
                false
            else
                store[key] <- { ExpiresAt = bannedAt.Add ttl; SeedChatId = chatId; SeedMessageId = messageId }
                true

        member _.TryGet(text, now) =
            let key = normalize text
            if key.Length = 0 then
                None
            else
                match store.TryGetValue key with
                | true, entry when entry.ExpiresAt > now ->
                    Some { SeedChatId = entry.SeedChatId; SeedMessageId = entry.SeedMessageId }
                | true, _ ->
                    // Expired — best-effort eviction so the dictionary doesn't grow unbounded.
                    // A lost race under concurrent access just means we re-check next hit.
                    store.TryRemove key |> ignore
                    None
                | false, _ ->
                    None

        member _.Clear() = store.Clear()
