namespace FakeAzureOcrApi

open System.Text.RegularExpressions

/// Deterministic-from-text fake embeddings for AlitaBot's memory foundation (Slice 5a).
///
/// Scheme ("hashed bag of words"): tokenize the input (lowercase, Unicode letter/digit
/// runs — handles Cyrillic and Latin alike), hash each token to one of `Dim` dimensions
/// (FNV-1a mod Dim), and add a fixed weight to that dimension per occurrence. The
/// resulting vector is L2-normalized so cosine similarity between two texts behaves like
/// a real embedding model would for tests: texts sharing vocabulary land close together
/// (nonzero dot product on the shared dimensions), texts with disjoint vocabulary land
/// near-orthogonal (~0 similarity, modulo rare hash collisions) — enough to assert
/// "the relevant message is in the /ask context, the irrelevant one isn't" deterministically,
/// without needing a real embedding model or scripted per-text vectors.
module Embedding =
    /// Matches AlitaBot's EMBEDDING_DEPLOYMENT vector(1536) column (V3 migration).
    [<Literal>]
    let Dim = 1536

    let private tokenPattern = Regex(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled)

    let private fnv1a (s: string) : uint32 =
        let mutable hash = 2166136261u
        for ch in s do
            hash <- (hash ^^^ uint32 ch) * 16777619u
        hash

    /// Tokenizes and hashes `text` into an L2-normalized `Dim`-length vector. Empty/
    /// whitespace-only text (or text with no letter/digit tokens) yields an all-zero
    /// vector — cosine similarity against it is 0 for anything, never NaN (division by
    /// a zero norm is guarded).
    let embed (text: string) : float32[] =
        let vec = Array.zeroCreate<float32> Dim
        for m in tokenPattern.Matches(text.ToLowerInvariant()) do
            let idx = int (fnv1a m.Value % uint32 Dim)
            vec[idx] <- vec[idx] + 1.0f
        let norm = sqrt (vec |> Array.sumBy (fun x -> x * x))
        if norm > 0.0f then
            for i in 0 .. Dim - 1 do
                vec[i] <- vec[i] / norm
        vec
