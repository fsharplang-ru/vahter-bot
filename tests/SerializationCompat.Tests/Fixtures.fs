namespace SerializationCompat.Tests

open System
open System.IO
open Xunit

/// Shared helpers: fixture loading + the two JSON stacks under test.
module Fixtures =

    /// Funogram's composed JsonSerializerOptions (snake_case + DU/unix-date/option
    /// converters). Using the exact same instance the library serializes with
    /// guarantees these tests can never drift from Funogram's real behavior.
    let funogramOptions = Funogram.Tools.options

    let private fixturesRoot =
        Path.Combine(AppContext.BaseDirectory, "fixtures")

    let private loadDir sub =
        Directory.GetFiles(Path.Combine(fixturesRoot, sub), "*.json")
        |> Array.map (fun f -> Path.GetFileNameWithoutExtension f, File.ReadAllText f)
        |> Array.sortBy fst

    /// All message fixtures except empty-raw-message: post-V40 backfill rows carry
    /// "{}" as their rawMessage CONTENT (no fields at all), so that fixture gets its
    /// own dedicated tolerance test instead of the field-level theories.
    let messageFixtures () =
        loadDir "messages" |> Array.filter (fun (n, _) -> n <> "empty-raw-message")

    let emptyRawMessageFixture () =
        loadDir "messages" |> Array.find (fun (n, _) -> n = "empty-raw-message") |> snd
    let callbackFixtures () = loadDir "callbacks"

    let messageFixture name =
        messageFixtures () |> Array.find (fun (n, _) -> n = name) |> snd

    /// xunit MemberData source: fixture names only (content re-read in the test
    /// so failures name the offending file).
    let messageFixtureNames () : TheoryData<string> =
        let data = TheoryData<string>()
        for name, _ in messageFixtures () do data.Add name
        data

type FixtureSanityTests() =
    [<Fact>]
    member _.``message fixtures are present`` () =
        Assert.NotEmpty(Fixtures.messageFixtures ())

    [<Fact>]
    member _.``callback fixtures are present`` () =
        Assert.NotEmpty(Fixtures.callbackFixtures ())
