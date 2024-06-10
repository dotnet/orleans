namespace TestFSharpSerialization

open TestExtensions
open UnitTests.FSharpTypes
open Xunit

[<CollectionDefinition("DefaultCluster")>]
type DefaultClusterTestCollection() = interface ICollectionFixture<DefaultClusterFixture>;

type FSharpSerializationTests(fixture: DefaultClusterFixture) =
    inherit HostedTestClusterEnsureDefaultStarted(fixture)

    let cluster = fixture.HostedCluster

    [<Fact; TestCategory("BVT"); TestCategory("Serialization")>]
    let Serialization_Roundtrip_FSharp_Unit () =
        let roundtripped = cluster.RoundTripSerializationForTesting ()
        let copy = cluster.DeepCopy ()
        Assert.Equal((), roundtripped)
        Assert.Equal((), copy)

    [<Fact; TestCategory("BVT"); TestCategory("Serialization")>]
    let Serialization_Roundtrip_FSharp_SingleCaseDiscriminatedUnion () =
        let du = SingleCaseDU.Case1 1
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

    [<Fact(Skip = "DUs with 4 or more cases fail when trying to instantiate Case{2-4}-classes via RuntimeHelpers.GetUninitializedObject when deserializing")>]
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
        Assert.Equal(case4, copyCase4);