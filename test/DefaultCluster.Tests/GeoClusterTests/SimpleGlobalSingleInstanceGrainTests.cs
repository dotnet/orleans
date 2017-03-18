using System.Threading.Tasks;
using TestExtensions;
using TestGrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.GeoClusterTests
{
    public class SimpleGlobalSingleInstanceGrainTests : HostedTestClusterEnsureDefaultStarted
    {
        private const string SimpleGrainNamePrefix = "UnitTests.Grains.SimpleG";

        public SimpleGlobalSingleInstanceGrainTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        public ISimpleGlobalSingleInstanceGrain GetGlobalSingleInstanceGrain()
        {
            return this.GrainFactory.GetGrain<ISimpleGlobalSingleInstanceGrain>(GetRandomGrainId(), SimpleGrainNamePrefix);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("GeoCluster")]
        public async Task SimpleGlobalSingleInstanceGrainTest()
        {
            int i = 0;
            while (i++ < 100)
            {
                ISimpleGlobalSingleInstanceGrain grain = GetGlobalSingleInstanceGrain();
                int r1 = random.Next(0, 100);
                int r2 = random.Next(0, 100);
                await grain.SetA(r1);
                await grain.SetB(r2);
                int result = await grain.GetAxB();
                Assert.Equal(r1 * r2, result);
            }
        }
    }
}
