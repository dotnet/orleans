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

[<Serializable>]
type DiscriminatedUnion = 
    | ArrayFieldCase of int array
    | ListFieldCase of int list
    | MapFieldCase of Map<int,string>
    | SetFieldCase of Set<int>

    static member array l = ArrayFieldCase l
    static member emptyArray() = ArrayFieldCase [||]
    static member nonEmptyArray() = ArrayFieldCase [|1; 2; 3|]

    static member list l = ListFieldCase l
    static member emptyList() = ListFieldCase []
    static member nonEmptyList() = ListFieldCase [1; 2; 3]

    static member set s = SetFieldCase s
    static member emptySet() = SetFieldCase Set.empty
    static member nonEmptySet() = Set.ofList [1; 2; 3] |> SetFieldCase 

    static member map m = MapFieldCase m
    static member emptyMap() = MapFieldCase Map.empty
    static member nonEmptyMap() = Map.ofList [0, "zero"; 1, "one"] |> MapFieldCase 
