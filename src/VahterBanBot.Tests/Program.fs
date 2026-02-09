module internal VahterBanBot.Tests.AssemblyInfo

open Xunit
open VahterBanBot.Tests.ContainerTestBase

[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
[<assembly: AssemblyFixture(typeof<MlDisabledVahterTestContainers>)>]
[<assembly: AssemblyFixture(typeof<MlEnabledVahterTestContainers>)>]
do ()
