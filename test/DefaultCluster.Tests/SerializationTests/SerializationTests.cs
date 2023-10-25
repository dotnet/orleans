using TestExtensions;
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
    }
}
