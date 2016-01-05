using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Tester;

namespace UnitTests.General
{
    [TestClass]
    public class LoadSheddingTest : UnitTestSiloHost
    {
        public LoadSheddingTest()
            : base(new TestingSiloOptions { StartFreshOrleans = true })
        {
        }

        [TestCleanup]
        public void Cleanup()
        {
            StopAllSilos();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("LoadShedding")]
        public void LoadSheddingBasic()
        {
            ISimpleGrain grain = GrainClient.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), SimpleGrain.SimpleGrainNamePrefix);
            
            Primary.Silo.Metrics.LatchIsOverload(true);
            Assert.IsTrue(Primary.Silo.Metrics.IsOverloaded, "Primary silo did not successfully latch overload state");

            var promise = grain.SetA(5);
            bool failed = false;
            try
            {
                promise.Wait();
            }
            catch (Exception)
            {
                failed = true;
            }
            Assert.IsTrue(failed, "Message was accepted even though silo was in overload state");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("LoadShedding")]
        public void LoadSheddingComplex()
        {
            ISimpleGrain grain = GrainClient.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), SimpleGrain.SimpleGrainNamePrefix);
            
            Console.WriteLine("Acquired grain reference");

            var promise = grain.SetA(1);
            try
            {
                promise.Wait();
            }
            catch (Exception ex)
            {
                Assert.Fail("Simple request failed with exception: " + ex);
            }

            Console.WriteLine("First set succeeded");

            Primary.Silo.Metrics.LatchIsOverload(true);
            Assert.IsTrue(Primary.Silo.Metrics.IsOverloaded, "Primary silo did not successfully latch overload state");

            promise = grain.SetA(2);
            try
            {
                promise.Wait();
                Assert.Fail("Message was accepted even though silo was in overload state");
            }
            catch (Exception ex)
            {
                var exc = ex.GetBaseException();
                if (!(exc is GatewayTooBusyException))
                {
                    Assert.Fail("Incorrect exception thrown for load-shed rejection: " + exc);
                }
            }

            Console.WriteLine("Second set was shed");

            Primary.Silo.Metrics.LatchIsOverload(false);
            Assert.IsFalse(Primary.Silo.Metrics.IsOverloaded, "Primary silo did not successfully latch non-overload state");

            promise = grain.SetA(4);
            try
            {
                promise.Wait();
            }
            catch (Exception ex)
            {
                Assert.Fail("Simple request after overload is cleared failed with exception: " + ex);
            }

            Console.WriteLine("Third set succeeded");
        }
    }
}
