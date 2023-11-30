namespace UnitTests.FSharpTypes

open Orleans

[<Immutable; GenerateSerializer>]
type SingleCaseDU =
    | Case1 of int
    static member ofInt i = Case1 i

[<Immutable; GenerateSerializer>]
type DoubleCaseDU =
    | Case1 of string
    | Case2 of int

[<Immutable; GenerateSerializer>]
type TripleCaseDU =
    | Case1 of string
    | Case2 of int
    | Case3 of char

[<Immutable; GenerateSerializer>]
type QuadrupleCaseDU =
    | Case1 of string
    | Case2 of int
    | Case3 of char
    | Case4 of byte

[<Immutable; GenerateSerializer>]
type Record = {  [<Id(1u)>] A: SingleCaseDU } with
    static member ofInt x = { A = SingleCaseDU.ofInt x }

[<Immutable; GenerateSerializer>]
type RecordOfIntOption = {  [<Id(1u)>] A: int option } with
    static member Empty = { A = None }
    static member ofInt x = { A = Some x}

[<Immutable; GenerateSerializer>]
type RecordOfIntOptionWithNoAttributes = {  [<Id(1u)>] A: int option } with
    static member Empty = { A = None }
    static member ofInt x = { A = Some x}

[<Immutable; GenerateSerializer>]
type GenericRecord<'T> = { [<Id(1u)>] Value: 'T } with
    static member ofT x = { Value = x }

[<Immutable; GenerateSerializer>]
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
