using System;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;

namespace UnitTests.General
{
    /// <summary>
    /// Summary description for SimpleGrain
    /// </summary>
    public class StateClassTests : HostedTestClusterEnsureDefaultStarted
    {
        private readonly Random rand = new Random();

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task StateClassTests_StateClass()
        {
            await StateClassTests_Test("UnitTests.Grains.SimplePersistentGrain");
        }

        private async Task StateClassTests_Test(string grainClass)
        {
            var grain = GrainFactory.GetGrain<ISimplePersistentGrain>(GetRandomGrainId(), grainClass);
            var originalVersion = await grain.GetVersion();
            await grain.SetA(98, true); // deactivate grain after setting A

            var newVersion = await grain.GetVersion(); // get a new version from the new activation
            Assert.NotEqual(originalVersion, newVersion);
            var a = await grain.GetA();
            Assert.Equal(98, a); // value of A survive deactivation and reactivation of the grain
        }
    }
}
