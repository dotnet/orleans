using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

namespace UnitTests.General
{
    [TestClass]
    public class DeactivationTests : UnitTestSiloHost
    {
        private readonly Random rand = new Random();

        public DeactivationTests()
            : base(new TestingSiloOptions { StartPrimary = true, StartSecondary = false })
        {
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional")]
        public async Task DeactivateReactivateTiming()
        {
            var x = rand.Next();
            var grain = GrainFactory.GetGrain<ISimplePersistentGrain>(x);
            var originalVersion = await grain.GetVersion();

            var sw = Stopwatch.StartNew();

            await grain.SetA(x, true); // deactivate grain after setting A
            var newVersion = await grain.GetVersion(); // get a new version from the new activation
            Assert.AreNotEqual(originalVersion, newVersion);

            sw.Stop();

            Assert.IsTrue(sw.ElapsedMilliseconds < 1000);
            logger.Info("Took {0}ms to deactivate and reactivate the grain", sw.ElapsedMilliseconds);

            var a = await grain.GetA();
            Assert.AreEqual(x, a); // value of A survive deactivation and reactivation of the grain
        }
    }
}
