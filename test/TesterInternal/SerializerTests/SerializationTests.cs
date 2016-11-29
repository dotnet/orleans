﻿using Orleans.Serialization;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests
{
    public class SerializationTests : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
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

            object obj = SerializationManager.DeepCopy(data);
            Assert.IsAssignableFrom<LargeTestData>(obj);

            object copy = SerializationManager.RoundTripSerializationForTesting(obj);
            Assert.IsAssignableFrom<LargeTestData>(copy);
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialization_ValueTypePhase1()
        {
            ValueTypeTestData data = new ValueTypeTestData(4);

            object obj = SerializationManager.DeepCopy(data);

            Assert.IsAssignableFrom<ValueTypeTestData>(obj);
            Assert.Equal<int>(4, ((ValueTypeTestData)obj).GetValue());
        }

        [Fact, TestCategory("Serialization")]
        public void Serialization_ValueTypePhase2()
        {
            ValueTypeTestData data = new ValueTypeTestData(4);

            object copy = SerializationManager.RoundTripSerializationForTesting(data);

            Assert.IsAssignableFrom<ValueTypeTestData>(copy);
            Assert.Equal<int>(4, ((ValueTypeTestData)copy).GetValue());
        }
    }
}
