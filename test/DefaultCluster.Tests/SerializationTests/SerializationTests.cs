using System;
using NodaTime;
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

            object obj = this.HostedCluster.DeepCopy(data);
            Assert.IsAssignableFrom<LargeTestData>(obj);

            object copy = this.HostedCluster.RoundTripSerializationForTesting(obj);
            Assert.IsAssignableFrom<LargeTestData>(copy);
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void Serialization_ValueTypePhase1()
        {
            ValueTypeTestData data = new ValueTypeTestData(4);

            object obj = this.HostedCluster.DeepCopy(data);

            Assert.IsAssignableFrom<ValueTypeTestData>(obj);
            Assert.Equal<int>(4, ((ValueTypeTestData)obj).GetValue());
        }

        [Fact, TestCategory("Serialization")]
        public void Serialization_ValueTypePhase2()
        {
            ValueTypeTestData data = new ValueTypeTestData(4);

            object copy = this.HostedCluster.RoundTripSerializationForTesting(data);

            Assert.IsAssignableFrom<ValueTypeTestData>(copy);
            Assert.Equal<int>(4, ((ValueTypeTestData)copy).GetValue());
        }
    }
}
