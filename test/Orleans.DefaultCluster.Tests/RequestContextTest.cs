using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTDefaultCluster.Tests.General
{
    /// <summary>
    /// Tests for Orleans Request Context functionality.
    /// Request Context allows flowing ambient data (like correlation IDs, user context,
    /// or request metadata) along with grain calls without explicitly passing it as
    /// parameters. This is Orleans' equivalent of thread-local storage or async-local
    /// storage, but designed for distributed calls across grains and silos.
    /// </summary>
    public class RequestContextTests : HostedTestClusterEnsureDefaultStarted
    {
        public RequestContextTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        /// <summary>
        /// Tests request context flowing from caller to callee.
        /// Verifies that data set in RequestContext before a grain call
        /// is available within the grain method execution, demonstrating
        /// the automatic propagation of ambient context across grain boundaries.
        /// This is useful for correlation IDs, authentication tokens, etc.
        /// </summary>
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

            Assert.Contains("GrainInfo", RequestContext.Keys);
        }

        /// <summary>
        /// Tests request context flowing from callee back to caller.
        /// Would verify that data set in RequestContext within a grain method
        /// is available to the caller after the call completes.
        /// NOTE: This test is currently skipped as the feature may not be fully supported.
        /// Request context typically flows one-way (caller to callee) for isolation.
        /// </summary>
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

            Assert.Contains("GrainInfo", RequestContext.Keys);
        }
    }
}