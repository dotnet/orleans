using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UnitTestGrainInterfaces;

namespace UnitTests.SerializerTests
{
    [TestClass]
    public class RoundTripSerializerTests : UnitTestBase
    {
        public RoundTripSerializerTests() : base(false)
        {}

        [TestMethod, TestCategory("Nightly"), TestCategory("Serialization")]
        public void Serialize_TestMethodResultEnum()
        {
            var grain = EnumResultGrainFactory.GetGrain(GetRandomGrainId());
            try
            {
                CampaignEnemyTestType result = grain.GetEnemyType().Result;
                Assert.AreEqual(CampaignEnemyTestType.Enemy2, result, "Enum return value wasn't transmitted properly");
            }
            catch (Exception exception)
            {
                Assert.Fail("Call to grain method with enum return threw exception: " + exception);
            }
        }
    }
}
