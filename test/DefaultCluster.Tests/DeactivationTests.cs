using System.Diagnostics;
using System.Threading.Tasks;
using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    public class DeactivationTests : HostedTestClusterEnsureDefaultStarted
    {
        public DeactivationTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact, TestCategory("BVT")]
        public async Task DeactivateReactivateTiming()
        {
            var x = GetRandomGrainId();
            var grain = this.GrainFactory.GetGrain<ISimplePersistentGrain>(x);
            var originalVersion = await grain.GetVersion();

            var sw = Stopwatch.StartNew();

            await grain.SetA(99, true); // deactivate grain after setting A value
            var newVersion = await grain.GetVersion(); // get a new version from the new activation
            Assert.NotEqual(originalVersion, newVersion);

            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 1000);
            this.Logger.Info("Took {0}ms to deactivate and reactivate the grain", sw.ElapsedMilliseconds);

            var a = await grain.GetA();
            Assert.Equal(99, a); // value of A survive deactivation and reactivation of the grain
        }
    }
}
