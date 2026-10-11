module VahterBanBot.Tests.AssemblyInfo

open System.Collections.Generic
open Xunit
open Xunit.v3
open VahterBanBot.Tests.ContainerTestBase

/// Stable test ordering makes shared-fixture failures reproducible across platforms.
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
[<assembly: AssemblyFixture(typeof<ReportCommandRegisteredTestContainers>)>]
[<assembly: AssemblyFixture(typeof<MlEnabledVahterTestContainers>)>]
[<assembly: AssemblyFixture(typeof<MlTrainingFromScratchTestContainers>)>]
[<assembly: AssemblyFixture(typeof<LlmVerdictCacheGlobalDisabledTestContainers>)>]
[<assembly: AssemblyFixture(typeof<LlmContentFilterDisabledTestContainers>)>]
[<assembly: AssemblyFixture(typeof<SpamTextCacheEnforceTestContainers>)>]
[<assembly: AssemblyFixture(typeof<SpamTextCacheShadowTestContainers>)>]
do ()
