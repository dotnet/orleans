using System;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using UnitTests.Tester;

namespace UnitTests.General
{
    public class LoadSheddingTest : HostedTestClusterPerTest
    {
        [Fact, TestCategory("Functional"), TestCategory("LoadShedding")]
        public void LoadSheddingBasic()
        {
            ISimpleGrain grain = GrainClient.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), SimpleGrain.SimpleGrainNamePrefix);

            this.HostedCluster.Primary.Silo.Metrics.LatchIsOverload(true);
            Assert.IsTrue(this.HostedCluster.Primary.Silo.Metrics.IsOverloaded, "Primary silo did not successfully latch overload state");

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

        [Fact, TestCategory("Functional"), TestCategory("LoadShedding")]
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

            this.HostedCluster.Primary.Silo.Metrics.LatchIsOverload(true);
            Assert.IsTrue(this.HostedCluster.Primary.Silo.Metrics.IsOverloaded, "Primary silo did not successfully latch overload state");

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

            this.HostedCluster.Primary.Silo.Metrics.LatchIsOverload(false);
            Assert.IsFalse(this.HostedCluster.Primary.Silo.Metrics.IsOverloaded, "Primary silo did not successfully latch non-overload state");

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
