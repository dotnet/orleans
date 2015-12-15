using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;


namespace UnitTests.General
{
    [TestClass]
    public class RequestContextTests : UnitTestSiloHost
    {
        public RequestContextTests()
            : base(new TestingSiloOptions { StartPrimary = true, StartSecondary = false })
        {
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        [TestMethod, TestCategory("RequestContext"), TestCategory("Functional")]
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

        [TestMethod, TestCategory("RequestContext"), TestCategory("Functional")]
        [ExpectedException(typeof(AssertFailedException))]
        public async Task RequestContextCalleeToCallerFlow()
        {
            var grain = GrainFactory.GetGrain<ISimplePersistentGrain>(random.Next());
            // This method in the grain does RequestContext.Set
            await grain.SetRequestContext(15);
            // Read the info set in the grain
            var infoFromGrain = RequestContext.Get("GrainInfo");
            Assert.IsNotNull(infoFromGrain);
            Assert.IsTrue((int)infoFromGrain == 15);
        }

    }
}
