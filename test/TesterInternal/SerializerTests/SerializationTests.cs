using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans.Serialization;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
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
            Assert.IsInstanceOfType(obj, typeof(LargeTestData), "Copied result is of wrong type");

            object copy = SerializationManager.RoundTripSerializationForTesting(obj);
            Assert.IsInstanceOfType(copy, typeof(LargeTestData), "Deserialized result is of wrong type");
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialization_ValueTypePhase1()
        {
            ValueTypeTestData data = new ValueTypeTestData(4);

            object obj = SerializationManager.DeepCopy(data);

            Assert.IsInstanceOfType(obj, typeof(ValueTypeTestData), "Deserialized result is of wrong type");
            Assert.AreEqual<int>(4, ((ValueTypeTestData)obj).GetValue(), "Deserialized result is incorrect");
        }

        [Fact, TestCategory("Serialization")]
        public void Serialization_ValueTypePhase2()
        {
            ValueTypeTestData data = new ValueTypeTestData(4);

            object copy = SerializationManager.RoundTripSerializationForTesting(data);

            Assert.IsInstanceOfType(copy, typeof(ValueTypeTestData), "Deserialized result is of wrong type");
            Assert.AreEqual<int>(4, ((ValueTypeTestData)copy).GetValue(), "Deserialized result is incorrect");
        }
    }
}
