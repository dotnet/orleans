using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTDefaultCluster.Tests.General
{
    public class RequestContextTests : HostedTestClusterEnsureDefaultStarted
    {
        public RequestContextTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact, TestCategory("BVT"), TestCategory("RequestContext")]
        public async Task RequestContextCallerToCalleeFlow()
        {
            var grain = this.GrainFactory.GetGrain<ISimplePersistentGrain>(Random.Shared.Next());
            // Set context to send to the grain
            RequestContext.Set("GrainInfo", 10);
            // This grain method reads the context and returns it
            var infoFromGrain = await grain.GetRequestContext();
            Assert.NotNull(infoFromGrain);
            Assert.True((int)infoFromGrain == 10);
        }

        [Fact(Skip = "Was failing before (just masked as a Pass), needs fixing or removing"), TestCategory("RequestContext"), TestCategory("Functional")]
        public async Task RequestContextCalleeToCallerFlow()
        {
            var grain = this.GrainFactory.GetGrain<ISimplePersistentGrain>(Random.Shared.Next());
            // This method in the grain does RequestContext.Set
            await grain.SetRequestContext(15);
            // Read the info set in the grain
            var infoFromGrain = RequestContext.Get("GrainInfo");
            Assert.NotNull(infoFromGrain);
            Assert.True((int)infoFromGrain == 15);
        }
    }
}