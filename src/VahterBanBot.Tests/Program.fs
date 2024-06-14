open Xunit
open Xunit.Extensions.AssemblyFixture

[<assembly: TestFramework(AssemblyFixtureFramework.TypeName, AssemblyFixtureFramework.AssemblyName)>]
do ()

module Program = let [<EntryPoint>] main _ = 0
