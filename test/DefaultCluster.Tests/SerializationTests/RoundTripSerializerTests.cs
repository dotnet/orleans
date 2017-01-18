﻿using System.Threading.Tasks;
using Orleans;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.SerializerTests
{
    [TestCategory("Serialization"), TestCategory("BVT"), TestCategory("Functional")]
    public class RoundTripSerializerTests : HostedTestClusterEnsureDefaultStarted
    {
        public RoundTripSerializerTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact]
        public async Task Serialize_TestMethodResultEnum()
        {
            var grain = this.GrainFactory.GetGrain<IRoundtripSerializationGrain>(GetRandomGrainId());
            CampaignEnemyTestType result = await grain.GetEnemyType();
            Assert.Equal(CampaignEnemyTestType.Enemy2, result); //Enum return value wasn't transmitted properly
        }

        [Fact]
        public async Task Serialize_TestMethodResultWithInheritedClosedGeneric()
        {
            var grain = this.GrainFactory.GetGrain<IRoundtripSerializationGrain>(GetRandomGrainId());
            object result = await grain.GetClosedGenericValue();
            Assert.NotNull(result);
        }
    }
}
