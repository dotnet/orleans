using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests
{
    /// <summary>
    /// Unit tests for circular call chains involving multiple grains
    /// </summary>
    public class CallChainTests : HostedTestClusterEnsureDefaultStarted
    {
        public CallChainTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact, TestCategory("Functional")]
        public async Task GenericGrainTests_ConcreteGrainWithGenericInterfaceGetGrain()
        {
            var grain = GrainFactory.GetGrain<ICallChainGrain1>(0);
            var result = await grain.Run(10);

            Assert.Equal(10, result);
        }
    }
}
