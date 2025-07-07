namespace TestFSharpSerialization

open TestExtensions
open UnitTests.FSharpTypes
open Xunit

[<CollectionDefinition("DefaultCluster")>]
type DefaultClusterTestCollection() = interface ICollectionFixture<DefaultClusterFixture>;

/// <summary>
/// Tests for Orleans' F# language support in serialization.
/// 
/// Orleans provides first-class support for F# types including:
/// - Discriminated unions (both enum-style and data-carrying)
/// - F# records with structural equality
/// - Recursive discriminated unions
/// - Generic discriminated unions
/// - Option types and other F# core types
/// - Unit type
/// 
/// The F# serialization support ensures:
/// - Proper handling of F# type system features
/// - Preservation of structural equality semantics
/// - Efficient encoding of discriminated union cases
/// - Support for F# collections (lists, sets, maps)
/// 
/// This enables F# developers to use Orleans with idiomatic F# code,
/// leveraging functional programming patterns while maintaining
/// full compatibility with Orleans' distributed computing model.
/// </summary>
type FSharpSerializationTests(fixture: DefaultClusterFixture) =
    inherit HostedTestClusterEnsureDefaultStarted(fixture)

    let cluster = fixture.HostedCluster

    /// <summary>
    /// Tests serialization of F# unit type, which represents "no value" and is commonly
    /// used in F# for side-effect-only operations.
    /// </summary>
    [<Fact; TestCategory("BVT"); TestCategory("Serialization")>]
    let Serialization_Roundtrip_FSharp_Unit () =
        let roundtripped = cluster.RoundTripSerializationForTesting ()
        let copy = cluster.DeepCopy ()
        Assert.Equal((), roundtripped)
        Assert.Equal((), copy)

    /// <summary>
    /// Tests enum-style discriminated unions (DUs without data), which are similar
    /// to C# enums but with F# type safety and pattern matching support.
    /// </summary>
    [<Fact; TestCategory("BVT"); TestCategory("Serialization")>]
    let Serialization_Roundtrip_FSharp_EnumStyleDU () =
        let case1 = EnumStyleDU.Case1
        let case2 = EnumStyleDU.Case2

        let roundtrippedCase1 = cluster.RoundTripSerializationForTesting case1
        let roundtrippedCase2 = cluster.RoundTripSerializationForTesting case2
        let copyCase1 = cluster.DeepCopy case1
        let copyCase2 = cluster.DeepCopy case2
        Assert.Equal(case1, roundtrippedCase1)
        Assert.Equal(case2, roundtrippedCase2)
        Assert.Equal(case1, copyCase1)
        Assert.Equal(case2, copyCase2)

    [<Fact; TestCategory("BVT"); TestCategory("Serialization")>]
    let Serialization_Roundtrip_FSharp_MixCaseDU () =
        let case1 = MixCaseDU.Case1
        let case2 = MixCaseDU.Case2 "Case2"

        let roundtrippedCase1 = cluster.RoundTripSerializationForTesting case1
        let roundtrippedCase2 = cluster.RoundTripSerializationForTesting case2
        let copyCase1 = cluster.DeepCopy case1
        let copyCase2 = cluster.DeepCopy case2
        Assert.Equal(case1, roundtrippedCase1)
        Assert.Equal(case2, roundtrippedCase2)
        Assert.Equal(case1, copyCase1)
        Assert.Equal(case2, copyCase2)

    /// <summary>
    /// Tests recursive discriminated unions, which can contain references to themselves,
    /// commonly used for tree structures and recursive data types in F#.
    /// </summary>
    [<Fact; TestCategory("BVT"); TestCategory("Serialization")>]
    let Serialization_Roundtrip_FSharp_RecursiveDU () =
        let case1 = RecursiveDU.Case1
        let case2 = RecursiveDU.Case2 (RecursiveDU.Case2 RecursiveDU.Case1)

        let roundtrippedCase1 = cluster.RoundTripSerializationForTesting case1
        let roundtrippedCase2 = cluster.RoundTripSerializationForTesting case2
        let copyCase1 = cluster.DeepCopy case1
        let copyCase2 = cluster.DeepCopy case2
        Assert.Equal(case1, roundtrippedCase1)
        Assert.Equal(case2, roundtrippedCase2)
        Assert.Equal(case1, copyCase1)
        Assert.Equal(case2, copyCase2)

    [<Fact; TestCategory("BVT"); TestCategory("Serialization")>]
    let Serialization_Roundtrip_FSharp_GenericDU () =
        let case1String = GenericDU.Case1 "string"
        let case1Int = GenericDU.Case1 99
        let case1Case2 = GenericDU.Case1 GenericDU.Case2
        let case2 = GenericDU.Case2

        let roundtrippedCase1String = cluster.RoundTripSerializationForTesting case1String
        let roundtrippedCase1Int = cluster.RoundTripSerializationForTesting case1Int
        let roundtrippedCase1Case2 = cluster.RoundTripSerializationForTesting case1Case2
        let roundtrippedCase2 = cluster.RoundTripSerializationForTesting case2
        let copyCase1String = cluster.DeepCopy case1String
        let copyCase1Int = cluster.DeepCopy case1Int
        let copyCase1Case2 = cluster.DeepCopy case1Case2
        let copyCase2 = cluster.DeepCopy case2
        Assert.Equal(case1String, roundtrippedCase1String)
        Assert.Equal(case1Int, roundtrippedCase1Int)
        Assert.Equal(case1Case2, roundtrippedCase1Case2)
        Assert.Equal(case2, roundtrippedCase2)
        Assert.Equal(case1String, copyCase1String)
        Assert.Equal(case1Int, copyCase1Int)
        Assert.Equal(case1Case2, copyCase1Case2)
        Assert.Equal(case2, copyCase2)

    [<Fact; TestCategory("BVT"); TestCategory("Serialization")>]
    let Serialization_Roundtrip_FSharp_SingleCaseDiscriminatedUnion () =
        let du = SingleCaseDU.Case1 1
        let roundtripped = cluster.RoundTripSerializationForTesting du
        let copy = cluster.DeepCopy du
        Assert.Equal(du, roundtripped)
        Assert.Equal(du, copy)

    [<Fact; TestCategory("BVT"); TestCategory("Serialization")>]
    let Serialization_Roundtrip_FSharp_NamedFieldsSingleCaseDiscriminatedUnion () =
        let du = NamedFieldsSingleCaseDU.Case1 "str"
        let roundtripped = cluster.RoundTripSerializationForTesting du
        let copy = cluster.DeepCopy du
        Assert.Equal(du, roundtripped)
        Assert.Equal(du, copy)

    [<Fact; TestCategory("BVT"); TestCategory("Serialization")>]
    let Serialization_Roundtrip_FSharp_DoubleCaseDiscriminatedUnion () =
        let case1 = DoubleCaseDU.Case1 "case 1"
        let case2 = DoubleCaseDU.Case2 2

        let roundtrippedCase1 = cluster.RoundTripSerializationForTesting case1
        let roundtrippedCase2 = cluster.RoundTripSerializationForTesting case2
        let copyCase1 = cluster.DeepCopy case1
        let copyCase2 = cluster.DeepCopy case2

        Assert.Equal(case1, roundtrippedCase1)
        Assert.Equal(case2, roundtrippedCase2)
        Assert.Equal(case1, copyCase1)
        Assert.Equal(case2, copyCase2)

    [<Fact; TestCategory("BVT"); TestCategory("Serialization")>]
    let Serialization_Roundtrip_FSharp_NamedFieldsDoubleCaseDiscriminatedUnion () =
        let case1 = NamedFieldsDoubleCaseDU.Case1 ("case 1", 123)
        let case2 = NamedFieldsDoubleCaseDU.Case2 "case 2"

        let roundtrippedCase1 = cluster.RoundTripSerializationForTesting case1
        let roundtrippedCase2 = cluster.RoundTripSerializationForTesting case2
        let copyCase1 = cluster.DeepCopy case1
        let copyCase2 = cluster.DeepCopy case2

        Assert.Equal(case1, roundtrippedCase1)
        Assert.Equal(case2, roundtrippedCase2)
        Assert.Equal(case1, copyCase1)
        Assert.Equal(case2, copyCase2)

    [<Fact; TestCategory("BVT"); TestCategory("Serialization")>]
    let Serialization_Roundtrip_FSharp_TripleCaseDiscriminatedUnion () =
        let case1 = TripleCaseDU.Case1 "case 1"
        let case2 = TripleCaseDU.Case2 2
        let case3 = TripleCaseDU.Case3 'a'

        let roundtrippedCase1 = cluster.RoundTripSerializationForTesting case1
        let roundtrippedCase2 = cluster.RoundTripSerializationForTesting case2
        let roundtrippedCase3 = cluster.RoundTripSerializationForTesting case3
        let copyCase1 = cluster.DeepCopy case1
        let copyCase2 = cluster.DeepCopy case2
        let copyCase3 = cluster.DeepCopy case3

        Assert.Equal(case1, roundtrippedCase1)
        Assert.Equal(case2, roundtrippedCase2)
        Assert.Equal(case3, roundtrippedCase3)
        Assert.Equal(case1, copyCase1)
        Assert.Equal(case2, copyCase2)
        Assert.Equal(case3, copyCase3)

    [<Fact; TestCategory("BVT"); TestCategory("Serialization")>]
    let Serialization_Roundtrip_FSharp_NamedFieldsTripleCaseDiscriminatedUnion () =
        let case1 = NamedFieldsTripleCaseDU.Case1 "case 1"
        let case2 = NamedFieldsTripleCaseDU.Case2 ("case 2", 123uy)
        let case3 = NamedFieldsTripleCaseDU.Case3 ("one", "two", 3)

        let roundtrippedCase1 = cluster.RoundTripSerializationForTesting case1
        let roundtrippedCase2 = cluster.RoundTripSerializationForTesting case2
        let roundtrippedCase3 = cluster.RoundTripSerializationForTesting case3
        let copyCase1 = cluster.DeepCopy case1
        let copyCase2 = cluster.DeepCopy case2
        let copyCase3 = cluster.DeepCopy case3

        Assert.Equal(case1, roundtrippedCase1)
        Assert.Equal(case2, roundtrippedCase2)
        Assert.Equal(case3, roundtrippedCase3)
        Assert.Equal(case1, copyCase1)
        Assert.Equal(case2, copyCase2)
        Assert.Equal(case3, copyCase3)

    [<Fact>]
    [<TestCategory("BVT"); TestCategory("Serialization")>]
    let Serialization_Roundtrip_FSharp_QuadrupleCaseDiscriminatedUnion () =
        let case1 = QuadrupleCaseDU.Case1 "case 1"
        let case2 = QuadrupleCaseDU.Case2 2
        let case3 = QuadrupleCaseDU.Case3 'a'
        let case4 = QuadrupleCaseDU.Case4 1uy

        let roundtrippedCase1 = cluster.RoundTripSerializationForTesting case1
        let roundtrippedCase2 = cluster.RoundTripSerializationForTesting case2
        let roundtrippedCase3 = cluster.RoundTripSerializationForTesting case3
        let roundtrippedCase4 = cluster.RoundTripSerializationForTesting case4
        let copyCase1 = cluster.DeepCopy case1
        let copyCase2 = cluster.DeepCopy case2
        let copyCase3 = cluster.DeepCopy case3
        let copyCase4 = cluster.DeepCopy case4

        Assert.Equal(case1, roundtrippedCase1);
        Assert.Equal(case2, roundtrippedCase2);
        Assert.Equal(case3, roundtrippedCase3);
        Assert.Equal(case4, roundtrippedCase4);
        Assert.Equal(case1, copyCase1);
        Assert.Equal(case2, copyCase2);
        Assert.Equal(case3, copyCase3);
        Assert.Equal(case4, copyCase4)

    [<Fact>]
    [<TestCategory("BVT"); TestCategory("Serialization")>]
    let Serialization_Roundtrip_FSharp_QuintupleCaseDiscriminatedUnion () =
        let case1 = QuintupleCaseDU.Case1
        let case2 = QuintupleCaseDU.Case2 (2, "some string")
        let case3 = QuintupleCaseDU.Case3
        let case4 = QuintupleCaseDU.Case4 (1uy, 2L)
        let case5 = QuintupleCaseDU.Case5 "case 5"

        let roundtrippedCase1 = cluster.RoundTripSerializationForTesting case1
        let roundtrippedCase2 = cluster.RoundTripSerializationForTesting case2
        let roundtrippedCase3 = cluster.RoundTripSerializationForTesting case3
        let roundtrippedCase4 = cluster.RoundTripSerializationForTesting case4
        let roundtrippedCase5 = cluster.RoundTripSerializationForTesting case5
        let copyCase1 = cluster.DeepCopy case1
        let copyCase2 = cluster.DeepCopy case2
        let copyCase3 = cluster.DeepCopy case3
        let copyCase4 = cluster.DeepCopy case4
        let copyCase5 = cluster.DeepCopy case5

        Assert.Equal(case1, roundtrippedCase1);
        Assert.Equal(case2, roundtrippedCase2);
        Assert.Equal(case3, roundtrippedCase3);
        Assert.Equal(case4, roundtrippedCase4);
        Assert.Equal(case5, roundtrippedCase5);
        Assert.Equal(case1, copyCase1);
        Assert.Equal(case2, copyCase2);
        Assert.Equal(case3, copyCase3);
        Assert.Equal(case4, copyCase4)
        Assert.Equal(case5, copyCase5)

    [<Fact; TestCategory("BVT"); TestCategory("Serialization")>]
    let Serialization_Roundtrip_FSharp_PrivateConstructorDiscriminatedUnion () =
        let du = PrivateConstructorDU.SomeValue
        let roundtripped = cluster.RoundTripSerializationForTesting du
        let copy = cluster.DeepCopy du
        Assert.Equal(du, roundtripped)
        Assert.Equal(du, copy)

    [<Fact; TestCategory("BVT"); TestCategory("Serialization")>]
    let Serialization_Roundtrip_FSharp_PrivateConstructorDoubleCaseDiscriminatedUnion () =
        let case1 = PrivateConstructorDoubleCaseDU.One
        let case2 = PrivateConstructorDoubleCaseDU.Two

        let roundtrippedCase1 = cluster.RoundTripSerializationForTesting case1
        let roundtrippedCase2 = cluster.RoundTripSerializationForTesting case2
        let copyCase1 = cluster.DeepCopy case1
        let copyCase2 = cluster.DeepCopy case2

        Assert.Equal(case1, roundtrippedCase1)
        Assert.Equal(case2, roundtrippedCase2)
        Assert.Equal(case1, copyCase1)
        Assert.Equal(case2, copyCase2)

    [<Fact>]
    [<TestCategory("BVT"); TestCategory("Serialization")>]
    let Serialization_Roundtrip_FSharp_Mutually_Recursive_DU () =
        let mutually_case1 = DUMutually.Case1 1
        let mutually_case2_case1 = DUMutually.Case2 (DURecursive.Case1 (DUMutually.Case1 2))
        let mutually_case2_case2 =
            DUMutually.Case2 (DURecursive.Case2 (DURecursive.Case1 (DUMutually.Case1 3), DUMutually.Case2 (DURecursive.Case1 (DUMutually.Case1 1337))))

        let roundtripped_mutually_case1 = cluster.RoundTripSerializationForTesting mutually_case1
        let roundtripped_mutually_case2_case1 = cluster.RoundTripSerializationForTesting mutually_case2_case1
        let roundtripped_mutually_case2_case2 = cluster.RoundTripSerializationForTesting mutually_case2_case2
        let copy_mutually_case1 = cluster.DeepCopy mutually_case1
        let copy_mutually_case2_case1 = cluster.DeepCopy mutually_case2_case1
        let copy_mutually_case2_case2 = cluster.DeepCopy mutually_case2_case2

        Assert.Equal(mutually_case1, roundtripped_mutually_case1)
        Assert.Equal(mutually_case2_case1, roundtripped_mutually_case2_case1)
        Assert.Equal(mutually_case2_case2, roundtripped_mutually_case2_case2)
        Assert.Equal(mutually_case1, copy_mutually_case1)
        Assert.Equal(mutually_case2_case1, copy_mutually_case2_case1)
        Assert.Equal(mutually_case2_case2, copy_mutually_case2_case2)

    [<Fact; TestCategory("BVT"); TestCategory("Serialization")>]
    let Serialization_Roundtrip_FSharp_SingleCaseStructDiscriminatedUnion () =
        let du = SingleCaseStructDU.Case "string"
        let roundtripped = cluster.RoundTripSerializationForTesting du
        let copy = cluster.DeepCopy du
        Assert.Equal(du, roundtripped)
        Assert.Equal(du, copy)

    [<Fact>]
    [<TestCategory("BVT"); TestCategory("Serialization")>]
    let Serialization_Roundtrip_FSharp_MulticaseStructDiscriminatedUnion () =
        let case1 = MulticaseStructDU.Case1 "case 1"
        let case2 = MulticaseStructDU.Case2 "case 2"
        let case3 = MulticaseStructDU.Case3 123
        let case4 = MulticaseStructDU.Case4
        let case5 = MulticaseStructDU.Case5 123L

        let roundtrippedCase1 = cluster.RoundTripSerializationForTesting case1
        let roundtrippedCase2 = cluster.RoundTripSerializationForTesting case2
        let roundtrippedCase3 = cluster.RoundTripSerializationForTesting case3
        let roundtrippedCase4 = cluster.RoundTripSerializationForTesting case4
        let roundtrippedCase5 = cluster.RoundTripSerializationForTesting case5
        let copyCase1 = cluster.DeepCopy case1
        let copyCase2 = cluster.DeepCopy case2
        let copyCase3 = cluster.DeepCopy case3
        let copyCase4 = cluster.DeepCopy case4
        let copyCase5 = cluster.DeepCopy case5

        Assert.Equal(case1, roundtrippedCase1);
        Assert.Equal(case2, roundtrippedCase2);
        Assert.Equal(case3, roundtrippedCase3);
        Assert.Equal(case4, roundtrippedCase4);
        Assert.Equal(case5, roundtrippedCase5);
        Assert.Equal(case1, copyCase1);
        Assert.Equal(case2, copyCase2);
        Assert.Equal(case3, copyCase3);
        Assert.Equal(case4, copyCase4)
        Assert.Equal(case5, copyCase5)
