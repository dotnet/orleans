namespace Orleans.CodeGenerator.Tests.FSharpModels

open Orleans

[<Immutable; GenerateSerializer>]
type GenericDU<'T> =
    | Case1 of 'T
    | Case2

[<Immutable; GenerateSerializer>]
type GenericRecord<'T> = { [<Id(1u)>] Value: 'T }

[<Struct; GenerateSerializer>]
type MulticaseStructDU =
    | Case1 of value1: string
    | Case2 of value2: string
    | Case3 of valueInt: int
    | Case4
    | Case5 of int64
