using System;
using NodaTime;
using Orleans.Serialization;
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

            object obj = this.HostedCluster.SerializationManager.DeepCopy(data);
            Assert.IsAssignableFrom<LargeTestData>(obj);

            object copy = this.HostedCluster.SerializationManager.RoundTripSerializationForTesting(obj);
            Assert.IsAssignableFrom<LargeTestData>(copy);
        }

        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void Serialization_ValueTypePhase1()
        {
            ValueTypeTestData data = new ValueTypeTestData(4);

            object obj = this.HostedCluster.SerializationManager.DeepCopy(data);

            Assert.IsAssignableFrom<ValueTypeTestData>(obj);
            Assert.Equal<int>(4, ((ValueTypeTestData)obj).GetValue());
        }

        [Fact, TestCategory("Serialization")]
        public void Serialization_ValueTypePhase2()
        {
            ValueTypeTestData data = new ValueTypeTestData(4);

            object copy = this.HostedCluster.SerializationManager.RoundTripSerializationForTesting(data);

            Assert.IsAssignableFrom<ValueTypeTestData>(copy);
            Assert.Equal<int>(4, ((ValueTypeTestData)copy).GetValue());
        }

        [Serializable]
        public class NodaTimeTestPoco
        {
            public LocalDate Date { get; }

            public NodaTimeTestPoco(LocalDate date)
            {
                this.Date = date;
            }
        }

        /// <summary>
        /// Regression test for https://github.com/dotnet/orleans/issues/2979.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public void Serialization_NodaTime()
        {
            var data = new NodaTimeTestPoco(new LocalDate(2010, 10, 14));

            object obj = this.HostedCluster.SerializationManager.RoundTripSerializationForTesting(data);

            Assert.IsAssignableFrom<NodaTimeTestPoco>(obj);
            Assert.Equal<LocalDate>(new LocalDate(2010, 10, 14), ((NodaTimeTestPoco)obj).Date);
        }
    }
}
