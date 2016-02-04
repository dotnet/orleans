using System.Threading.Tasks;
using Xunit;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using System;
using Tester;

namespace UnitTests.General
{
    public class RequestContextTests : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("RequestContext"), TestCategory("Functional")]
        public async Task RequestContextCallerToCalleeFlow()
        {
            var grain = GrainFactory.GetGrain<ISimplePersistentGrain>(random.Next());
            // Set context to send to the grain
            RequestContext.Set("GrainInfo", 10);
            // This grain method reads the context and returns it
            var infoFromGrain = await grain.GetRequestContext();
            Assert.IsNotNull(infoFromGrain);
            Assert.IsTrue((int)infoFromGrain == 10);
        }

        [Fact, TestCategory("RequestContext"), TestCategory("Functional")]
        public async Task RequestContextCalleeToCallerFlow()
        {
            await Xunit.Assert.ThrowsAsync(typeof(Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException), async () =>
            {
                var grain = GrainFactory.GetGrain<ISimplePersistentGrain>(random.Next());
                // This method in the grain does RequestContext.Set
                await grain.SetRequestContext(15);
                // Read the info set in the grain
                var infoFromGrain = RequestContext.Get("GrainInfo");
                Assert.IsNotNull(infoFromGrain);
                Assert.IsTrue((int)infoFromGrain == 15);
            });
        }

    }
}
