using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;


namespace UnitTests.General
{
    [TestClass]
    public class LoadSheddingTest : UnitTestBase
    {
        public LoadSheddingTest()
            : base(new Options { StartFreshOrleans = true })
        {
        }

        [TestCleanup]
        public void Cleanup()
        {
            ResetDefaultRuntimes();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("LoadShedding")]
        public void LoadSheddingBasic()
        {
            ISimpleGrain grain = TestConstants.GetSimpleGrain();
            
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

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("LoadShedding")]
        public void LoadSheddingComplex()
        {
            ISimpleGrain grain = TestConstants.GetSimpleGrain();
            
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
