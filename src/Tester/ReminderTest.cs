using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.TestingHost;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

namespace Tester
{
    [TestClass]
    public class ReminderTest : UnitTestSiloHost
    {
        public ReminderTest()
            : base(new TestingSiloOptions { StartPrimary = true, StartSecondary = false })
        {
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Reminders")]
        public async Task SimpleGrainGetGrain()
        {
            IReminderTestGrain grain = GrainFactory.GetGrain<IReminderTestGrain>(0);
            bool notExists = await grain.IsReminderExists("not exists");
            Assert.IsFalse(notExists);
        }
    }
}