using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

namespace UnitTests.General
{
    /// <summary>
    /// Summary description for SimpleGrain
    /// </summary>
    [TestClass]
    public class StateClassTests : UnitTestSiloHost
    {
        private readonly Random rand = new Random();

        public StateClassTests()
            : base(new TestingSiloOptions {StartPrimary = true, StartSecondary = false})
        {
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional")]
        public async Task StateClassTests_StateClass()
        {
            await StateClassTests_Test("UnitTests.Grains.SimplePersistentGrain");
        }

        private async Task StateClassTests_Test(string grainClass)
        {
            var x = rand.Next();
            var grain = GrainFactory.GetGrain<ISimplePersistentGrain>(x, grainClass);
            var originalVersion = await grain.GetVersion();
            await grain.SetA(x, true); // deactivate grain after setting A

            var newVersion = await grain.GetVersion(); // get a new version from the new activation
            Assert.AreNotEqual(originalVersion, newVersion);
            var a = await grain.GetA();
            Assert.AreEqual(x, a); // value of A survive deactivation and reactivation of the grain
        }
    }
}
