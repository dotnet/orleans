using System.Threading.Tasks;
using Orleans;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.SerializerTests
{
    [TestCategory("Serialization")]
    public class RoundTripSerializerTests : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("Functional")]
        public async Task Serialize_TestMethodResultEnum()
        {
            var grain = GrainClient.GrainFactory.GetGrain<IRoundtripSerializationGrain>(GetRandomGrainId());
            CampaignEnemyTestType result = await grain.GetEnemyType();
            Assert.Equal(CampaignEnemyTestType.Enemy2, result); //Enum return value wasn't transmitted properly
        }

        [Fact, TestCategory("Functional")]
        public async Task Serialize_TestMethodResultWithInheritedClosedGeneric()
        {
            var grain = GrainClient.GrainFactory.GetGrain<IRoundtripSerializationGrain>(GetRandomGrainId());
            object result = await grain.GetClosedGenericValue();
            Assert.NotNull(result);
        }
    }
}
