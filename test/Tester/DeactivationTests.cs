using System.Diagnostics;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;

namespace UnitTests.General
{
    public class DeactivationTests : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task DeactivateReactivateTiming()
        {
            var x = GetRandomGrainId();
            var grain = GrainFactory.GetGrain<ISimplePersistentGrain>(x);
            var originalVersion = await grain.GetVersion();

            var sw = Stopwatch.StartNew();

            await grain.SetA(99, true); // deactivate grain after setting A
            var newVersion = await grain.GetVersion(); // get a new version from the new activation
            Assert.AreNotEqual(originalVersion, newVersion);

            sw.Stop();

            Assert.IsTrue(sw.ElapsedMilliseconds < 1000);
            logger.Info("Took {0}ms to deactivate and reactivate the grain", sw.ElapsedMilliseconds);

            var a = await grain.GetA();
            Assert.AreEqual(99, a); // value of A survive deactivation and reactivation of the grain
        }
    }
}
