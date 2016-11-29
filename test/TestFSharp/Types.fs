namespace UnitTests.FSharpTypes

open System
open Orleans.Concurrency

[<Serializable; Immutable>]
type SingleCaseDU = 
    private 
    | SingleCaseDU of int
    static member ofInt i = SingleCaseDU i

[<Serializable; Immutable>]
type Record = { A: SingleCaseDU } with
    static member ofInt x = { A = SingleCaseDU.ofInt x }

[<Serializable; Immutable>]
type RecordOfIntOption = { A: int option } with
    static member Empty = { A = None }
    static member ofInt x = { A = Some x}

type RecordOfIntOptionWithNoAttributes = { A: int option } with
    static member Empty = { A = None }
    static member ofInt x = { A = Some x}

[<Serializable; Immutable>]
type GenericRecord<'T> = { Value: 'T } with
    static member ofT x = { Value = x }
