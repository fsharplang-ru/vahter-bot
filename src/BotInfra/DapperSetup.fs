namespace BotInfra

open System
open System.Data
open Dapper

/// Shared Dapper type handlers.
module DapperSetup =
    /// Dapper type handler for DateOnly (maps to PostgreSQL DATE).
    type DateOnlyTypeHandler() =
        inherit SqlMapper.TypeHandler<DateOnly>()
        override _.SetValue(parameter: IDbDataParameter, value: DateOnly) =
            parameter.Value <- value.ToDateTime(TimeOnly.MinValue)
        override _.Parse(value: obj) =
            match value with
            | :? DateOnly as d -> d
            | :? DateTime as dt -> DateOnly.FromDateTime(dt)
            | x -> failwithf "Unsupported DateOnly value: %A" x

    /// Register the DateOnly type handler with Dapper.
    let registerDateOnlyHandler () =
        SqlMapper.AddTypeHandler(DateOnlyTypeHandler())
