using TestExtensions;
using UnitTests.FSharpTypes;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests
{
    public class SerializationTests : HostedTestClusterEnsureDefaultStarted
    {
        public SerializationTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void Serialization_LargeTestData()
        {
            var data = new LargeTestData
                           {
                               Description =
                                   "This is a test. This is only a test. In the event of a real execution, this would contain actual data.",
                               EnumValue = TestEnum.First
                           };
            data.SetBit(13);
            data.SetEnemy(17, CampaignEnemyTestType.Enemy1);

            object obj = HostedCluster.DeepCopy(data);
            Assert.IsAssignableFrom<LargeTestData>(obj);

            object copy = HostedCluster.RoundTripSerializationForTesting(obj);
            Assert.IsAssignableFrom<LargeTestData>(copy);
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void Serialization_ValueType_Phase1()
        {
            ValueTypeTestData data = new ValueTypeTestData(4);

            object obj = HostedCluster.DeepCopy(data);

            Assert.IsAssignableFrom<ValueTypeTestData>(obj);
            Assert.Equal(4, ((ValueTypeTestData)obj).Value);
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void Serialization_ValueType_Phase2()
        {
            ValueTypeTestData data = new ValueTypeTestData(4);

            object copy = HostedCluster.RoundTripSerializationForTesting(data);

            Assert.IsAssignableFrom<ValueTypeTestData>(copy);
            Assert.Equal(4, ((ValueTypeTestData)copy).Value);
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void Serialization_DefaultActivatorValueTypeWithRequiredField_Phase1()
        {
            DefaultActivatorValueTypeWithRequiredField data = new(4);

            object obj = HostedCluster.DeepCopy(data);

            Assert.IsAssignableFrom<DefaultActivatorValueTypeWithRequiredField>(obj);
            Assert.Equal(4, ((DefaultActivatorValueTypeWithRequiredField)obj).Value);
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void Serialization_DefaultActivatorValueTypeWithRequiredField_Phase2()
        {
            DefaultActivatorValueTypeWithRequiredField data = new(4);

            object copy = HostedCluster.RoundTripSerializationForTesting(data);

            Assert.IsAssignableFrom<DefaultActivatorValueTypeWithRequiredField>(copy);
            Assert.Equal(4, ((DefaultActivatorValueTypeWithRequiredField)copy).Value);
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void Serialization_DefaultActivatorValueTypeWithUseActivator_Phase1()
        {
            DefaultActivatorValueTypeWithUseActivator data = new();

            object obj = HostedCluster.DeepCopy(data);

            Assert.IsAssignableFrom<DefaultActivatorValueTypeWithUseActivator>(obj);
            Assert.Equal(4, ((DefaultActivatorValueTypeWithUseActivator)obj).Value);
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void Serialization_DefaultActivatorValueTypeWithUseActivator_Phase2()
        {
            DefaultActivatorValueTypeWithUseActivator data = new();

            object copy = HostedCluster.RoundTripSerializationForTesting(data);

            Assert.IsAssignableFrom<DefaultActivatorValueTypeWithUseActivator>(copy);
            Assert.Equal(4, ((DefaultActivatorValueTypeWithUseActivator)copy).Value);
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void Serialization_Roundtrip_FSharp_SingleCaseDiscriminatedUnion()
        {
            var du = SingleCaseDU.ofInt(1);
            var copy = HostedCluster.RoundTripSerializationForTesting(du);

            Assert.IsAssignableFrom<SingleCaseDU>(copy);
            Assert.Equal(du, copy);
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void Serialization_Roundtrip_FSharp_DoubleCaseDiscriminatedUnion()
        {
            var case1 = DoubleCaseDU.NewCase1("case 1");
            var case2 = DoubleCaseDU.NewCase2(2);

            var copyCase1 = HostedCluster.RoundTripSerializationForTesting(case1);
            var copyCase2 = HostedCluster.RoundTripSerializationForTesting(case2);

            Assert.IsAssignableFrom<DoubleCaseDU>(copyCase1);
            Assert.IsAssignableFrom<DoubleCaseDU>(copyCase2);
            Assert.Equal(case1, copyCase1);
            Assert.Equal(case2, copyCase2);
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void Serialization_Roundtrip_FSharp_TripleCaseDiscriminatedUnion()
        {
            var case1 = TripleCaseDU.NewCase1("case 1");
            var case2 = TripleCaseDU.NewCase2(2);
            var case3 = TripleCaseDU.NewCase3('a');

            var copyCase1 = HostedCluster.RoundTripSerializationForTesting(case1);
            var copyCase2 = HostedCluster.RoundTripSerializationForTesting(case2);
            var copyCase3 = HostedCluster.RoundTripSerializationForTesting(case3);

            Assert.IsAssignableFrom<TripleCaseDU>(copyCase1);
            Assert.IsAssignableFrom<TripleCaseDU>(copyCase2);
            Assert.IsAssignableFrom<TripleCaseDU>(copyCase3);
            Assert.Equal(case1, copyCase1);
            Assert.Equal(case2, copyCase2);
            Assert.Equal(case3, copyCase3);
        }

        [Fact(Skip = "DUs with 4 or more cases fail when trying to instanciate Case{1-4}-classes via RuntimeHelpers.GetUninitializedObject when deserializing"),
         TestCategory("BVT"), TestCategory("Serialization")]
        public void Serialization_Roundtrip_FSharp_QuadrupleCaseDiscriminatedUnion()
        {
            var case1 = QuadrupleCaseDU.NewCase1("case 1");
            var case2 = QuadrupleCaseDU.NewCase2(2);
            var case3 = QuadrupleCaseDU.NewCase3('a');
            var case4 = QuadrupleCaseDU.NewCase4(1);

            var copyCase1 = HostedCluster.RoundTripSerializationForTesting(case1);
            var copyCase2 = HostedCluster.RoundTripSerializationForTesting(case2);
            var copyCase3 = HostedCluster.RoundTripSerializationForTesting(case3);
            var copyCase4 = HostedCluster.RoundTripSerializationForTesting(case4);

            Assert.IsAssignableFrom<QuadrupleCaseDU>(copyCase1);
            Assert.IsAssignableFrom<QuadrupleCaseDU>(copyCase2);
            Assert.IsAssignableFrom<QuadrupleCaseDU>(copyCase3);
            Assert.IsAssignableFrom<QuadrupleCaseDU>(copyCase4);
            Assert.Equal(case1, copyCase1);
            Assert.Equal(case2, copyCase2);
            Assert.Equal(case3, copyCase3);
            Assert.Equal(case4, copyCase4);
        }
    }
}