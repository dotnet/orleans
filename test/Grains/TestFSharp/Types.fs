namespace UnitTests.FSharpTypes

open System.Runtime.CompilerServices
open Orleans

[<Immutable; GenerateSerializer>]
type EnumStyleDU =
    | Case1
    | Case2
    | Case3

[<Immutable; GenerateSerializer>]
type MixCaseDU =
    | Case1
    | Case2 of string

[<Immutable; GenerateSerializer>]
type RecursiveDU =
    | Case1
    | Case2 of RecursiveDU

[<Immutable; GenerateSerializer>]
type GenericDU<'T> =
    | Case1 of 'T
    | Case2

[<Immutable; GenerateSerializer>]
type PrivateConstructorDU =
    private Constructor of string
        static member SomeValue =
            Constructor "some value"

[<Immutable; GenerateSerializer>]
type PrivateConstructorDoubleCaseDU =
    private
        | ConstructorOne of string
        | ConstructorTwo of value: int
    static member One =
        ConstructorOne "some one"
    static member Two =
        ConstructorTwo 2

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
type NamedFieldsSingleCaseDU =
    | Case1 of something: string

[<Immutable; GenerateSerializer>]
type NamedFieldsDoubleCaseDU =
    | Case1 of something: string * int
    | Case2 of other: string

[<Immutable; GenerateSerializer>]
type NamedFieldsTripleCaseDU =
    | Case1 of something: string
    | Case2 of other: string * byte
    | Case3 of string * second: string * third: int

[<Immutable; GenerateSerializer>]
type QuintupleCaseDU =
    | Case1
    | Case2 of number: int * string
    | Case3
    | Case4 of theByte: byte * theLong: int64
    | Case5 of string

[<Immutable; GenerateSerializer>]
type DUMutually =
    | Case1 of int
    | Case2 of DURecursive
and [<Immutable; GenerateSerializer>] DURecursive =
    | Case1 of DUMutually
    | Case2 of DURecursive * DUMutually

[<Struct; GenerateSerializer>]
type SingleCaseStructDU = Case of string

[<Struct; GenerateSerializer>]
type MulticaseStructDU =
    | Case1 of value1: string
    | Case2 of value2: string
    | Case3 of valueInt: int
    | Case4
    | Case5 of int64

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

[<InternalsVisibleTo("TestFSharpGrainInterfaces")>]
do ()
