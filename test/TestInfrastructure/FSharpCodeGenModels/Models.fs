namespace Orleans.CodeGenerator.Tests.FSharpModels

open Orleans

[<Immutable; GenerateSerializer>]
type GenericDU<'T> =
    | Case1 of 'T
    | Case2

[<Immutable; GenerateSerializer>]
type GenericRecord<'T> = { [<Id(1u)>] Value: 'T }
