module VahterBanBot.Tests.AssemblyInfo

open System.Collections.Generic
open Xunit
open Xunit.v3
open VahterBanBot.Tests.ContainerTestBase

/// Deterministic alphabetical test case orderer.
///
/// WHY THIS IS NEEDED:
/// Tests share a single database instance (via Testcontainers) and some tests mutate
/// global state — specifically the `false_positive_messages` and `false_negative_messages`
/// tables. The karma calculation query in DB.getUserStatsByLastNMessages joins against
/// these tables, so rows inserted by one test can change the spam/ham classification
/// for messages in a completely different test.
///
/// xUnit v3's DefaultTestCaseOrderer uses a hash-based "unpredictable but stable" order
/// that varies across platforms (the hash depends on the module version ID and pointer size).
/// This means test execution order differs between x64 Windows (local dev) and ARM64 Linux
/// (CI), causing tests to pass locally but fail on CI (or vice versa) when shared state
/// contamination is order-dependent.
///
/// This orderer sorts test cases alphabetically by display name, producing an identical
/// execution order on every platform, every run, making shared-state issues reproducible
/// everywhere rather than silently platform-specific.
type AlphabeticalTestCaseOrderer() =
    interface ITestCaseOrderer with
        member _.OrderTestCases(testCases: IReadOnlyCollection<'TTestCase>) : IReadOnlyCollection<'TTestCase> =
            testCases
            |> Seq.sortBy (fun tc -> tc.TestCaseDisplayName)
            |> Seq.toArray
            :> IReadOnlyCollection<_>

/// Same idea applied at the collection (≈ test class) level.
/// xUnit v3's DefaultTestCollectionOrderer randomizes collection order,
/// so without this the class execution order differs across platforms.
type AlphabeticalTestCollectionOrderer() =
    interface ITestCollectionOrderer with
        member _.OrderTestCollections(testCollections: IReadOnlyCollection<'TTestCollection>) : IReadOnlyCollection<'TTestCollection> =
            testCollections
            |> Seq.sortBy (fun tc -> tc.TestCollectionDisplayName)
            |> Seq.toArray
            :> IReadOnlyCollection<_>

[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
[<assembly: TestCaseOrdererAttribute(typeof<AlphabeticalTestCaseOrderer>)>]
[<assembly: TestCollectionOrdererAttribute(typeof<AlphabeticalTestCollectionOrderer>)>]
[<assembly: AssemblyFixture(typeof<MlDisabledVahterTestContainers>)>]
[<assembly: AssemblyFixture(typeof<MlEnabledVahterTestContainers>)>]
do ()
