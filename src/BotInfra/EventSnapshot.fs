namespace BotInfra

open System
open System.Collections.Generic
open Microsoft.FSharp.Reflection

/// Opt-in `EventStore` snapshotting: state = stored snapshot + events after it. Snapshots are a
/// disposable cache — deleting them is always safe, loads fall back to a full replay.
type SnapshotPolicy =
    { /// Aggregate discriminator, part of the snapshot key (one stream may carry several folds).
      StateType: string
      /// Must be bumped on ANY change to the state type's shape OR to its fold semantics;
      /// snapshots written under another version are ignored and rebuilt by replay.
      SchemaVersion: int
      /// A snapshot is (re)written once at least this many events sit past the stored one.
      SnapshotEvery: int }

/// Deterministic structure of an F# type, for pinning a snapshotted state's shape to its
/// `SnapshotPolicy.SchemaVersion` in a test. Anonymous records render their fields only.
module SnapshotShape =

    let private isAnonymousRecord (t: Type) = t.Name.Contains "AnonymousType"

    let rec private render (visiting: HashSet<Type>) (t: Type) : string =
        if t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>> then
            $"option<{render visiting (t.GetGenericArguments()[0])}>"
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<_>> then
            $"list<{render visiting (t.GetGenericArguments()[0])}>"
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Nullable<_>> then
            $"nullable<{render visiting (t.GetGenericArguments()[0])}>"
        elif t.IsArray then
            $"array<{render visiting (t.GetElementType())}>"
        elif FSharpType.IsTuple t then
            FSharpType.GetTupleElements t |> Array.map (render visiting) |> String.concat " * " |> sprintf "(%s)"
        elif FSharpType.IsRecord(t, true) || FSharpType.IsUnion(t, true) then
            let name = if isAnonymousRecord t then "" else t.Name
            if not (visiting.Add t) then $"@{name}"
            else
                let fields (ps: Reflection.PropertyInfo[]) =
                    ps |> Array.map (fun p -> $"{p.Name}: {render visiting p.PropertyType}") |> String.concat "; "
                let body =
                    if FSharpType.IsRecord(t, true) then
                        $"{{{fields (FSharpType.GetRecordFields(t, true))}}}"
                    else
                        FSharpType.GetUnionCases(t, true)
                        |> Array.map (fun c ->
                            match c.GetFields() with
                            | [||] -> c.Name
                            | fs -> $"{c.Name} of ({fields fs})")
                        |> String.concat " | "
                        |> sprintf "[%s]"
                %visiting.Remove t
                name + body
        elif t.IsGenericType then
            let name = t.Name.Substring(0, t.Name.IndexOf '`')
            let args = t.GetGenericArguments() |> Array.map (render visiting) |> String.concat ", "
            $"{name}<{args}>"
        elif t.IsEnum then
            let names = Enum.GetNames t |> String.concat ","
            $"{t.Name}<{names}>"
        else
            t.FullName

    /// Renders `t`'s structure; nested records/unions are expanded, recursion is cut with `@Name`.
    let describe (t: Type) : string = render (HashSet<Type>()) t
