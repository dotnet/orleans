using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

#pragma warning disable 618

namespace DefaultCluster.Tests.General
{
    public class BasicActivationTests : HostedTestClusterEnsureDefaultStarted
    {
        public BasicActivationTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        private TimeSpan GetResponseTimeout() => Client.ServiceProvider.GetRequiredService<OutsideRuntimeClient>().GetResponseTimeout();
        private void SetResponseTimeout(TimeSpan value) => Client.ServiceProvider.GetRequiredService<OutsideRuntimeClient>().SetResponseTimeout(value);

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public void BasicActivation_ActivateAndUpdate()
        {
            var g1Key = GetRandomGrainId();
            var g2Key = GetRandomGrainId();
            var g1 = GrainFactory.GetGrain<ITestGrain>(g1Key);
            var g2 = GrainFactory.GetGrain<ITestGrain>(g2Key);
            Assert.Equal(g1Key, g1.GetPrimaryKeyLong());
            Assert.Equal(g1Key, g1.GetKey().Result);
            Assert.Equal(g1Key.ToString(), g1.GetLabel().Result);
            Assert.Equal(g2Key, g2.GetKey().Result);
            Assert.Equal(g2Key.ToString(), g2.GetLabel().Result);

            g1.SetLabel("one").Wait();
            Assert.Equal("one", g1.GetLabel().Result);
            Assert.Equal(g2Key.ToString(), g2.GetLabel().Result);

            var g1a = GrainFactory.GetGrain<ITestGrain>(g1Key);
            Assert.Equal("one", g1a.GetLabel().Result);
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public void BasicActivation_Guid_ActivateAndUpdate()
        {
            var guid1 = Guid.NewGuid();
            var guid2 = Guid.NewGuid();

            var g1 = GrainFactory.GetGrain<IGuidTestGrain>(guid1);
            var g2 = GrainFactory.GetGrain<IGuidTestGrain>(guid2);
            Assert.Equal(guid1, g1.GetPrimaryKey());
            Assert.Equal(guid1, g1.GetKey().Result);
            Assert.Equal(guid1.ToString(), g1.GetLabel().Result);
            Assert.Equal(guid2, g2.GetKey().Result);
            Assert.Equal(guid2.ToString(), g2.GetLabel().Result);

            g1.SetLabel("one").Wait();
            Assert.Equal("one", g1.GetLabel().Result);
            Assert.Equal(guid2.ToString(), g2.GetLabel().Result);

            var g1a = GrainFactory.GetGrain<IGuidTestGrain>(guid1);
            Assert.Equal("one", g1a.GetLabel().Result);
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate"), TestCategory("ErrorHandling"), TestCategory("GetGrain")]
        public async Task BasicActivation_Fail()
        {
            bool failed;
            long key = 0;
            try
            {
                // Key values of -2 are not allowed in this case
                var fail = GrainFactory.GetGrain<ITestGrain>(-2);
                key = await fail.GetKey();
                failed = false;
            }
            catch (ArgumentException)
            {
                failed = true;
            }

            if (!failed) Assert.True(false, "Should have failed, but instead returned " + key);
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate"), TestCategory("ErrorHandling"), TestCategory("GetGrain")]
        public async Task BasicActivation_BurstFail()
        {
            bool failed;
            long key = 0;
            var tasks = new List<Task>();
            try
            {
                // Key values of -2 are not allowed in this case
                var fail = GrainFactory.GetGrain<ITestGrainLongOnActivateAsync>(-2);
                for (var i = 0; i < 10000; i++)
                {
                    tasks.Add(fail.GetKey());
                }
                failed = false;
                await Task.WhenAll(tasks);
            }
            catch (Exception)
            {
                failed = true;
                foreach (var t in tasks)
                {
                    Assert.Equal(typeof(ArgumentException), t.Exception.InnerException.GetType());
                }
            }

            if (!failed) Assert.True(false, "Should have failed, but instead returned " + key);
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public void BasicActivation_ULong_MaxValue()
        {
            var key1AsUlong = ulong.MaxValue; // == -1L
            var key1 = (long)key1AsUlong;

            var g1 = GrainFactory.GetGrain<ITestGrain>(key1);
            Assert.Equal(key1, g1.GetPrimaryKeyLong());
            Assert.Equal((long)key1AsUlong, g1.GetPrimaryKeyLong());
            Assert.Equal(key1, g1.GetKey().Result);
            Assert.Equal(key1.ToString(CultureInfo.InvariantCulture), g1.GetLabel().Result);

            g1.SetLabel("MaxValue").Wait();
            Assert.Equal("MaxValue", g1.GetLabel().Result);

            var g1a = GrainFactory.GetGrain<ITestGrain>((long)key1AsUlong);
            Assert.Equal("MaxValue", g1a.GetLabel().Result);
            Assert.Equal(key1, g1a.GetPrimaryKeyLong());
            Assert.Equal((long)key1AsUlong, g1a.GetKey().Result);
        }

        [Fact, TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public void BasicActivation_ULong_MinValue()
        {
            var key1AsUlong = ulong.MinValue; // == zero
            var key1 = (long)key1AsUlong;

            var g1 = GrainFactory.GetGrain<ITestGrain>(key1);
            Assert.Equal(key1, g1.GetPrimaryKeyLong());
            Assert.Equal((long)key1AsUlong, g1.GetPrimaryKeyLong());
            Assert.Equal(key1, g1.GetPrimaryKeyLong());
            Assert.Equal(key1, g1.GetKey().Result);
            Assert.Equal(key1.ToString(CultureInfo.InvariantCulture), g1.GetLabel().Result);

            g1.SetLabel("MinValue").Wait();
            Assert.Equal("MinValue", g1.GetLabel().Result);

            var g1a = GrainFactory.GetGrain<ITestGrain>((long)key1AsUlong);
            Assert.Equal("MinValue", g1a.GetLabel().Result);
            Assert.Equal(key1, g1a.GetPrimaryKeyLong());
            Assert.Equal((long)key1AsUlong, g1a.GetKey().Result);
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public void BasicActivation_Long_MaxValue()
        {
            long key1 = int.MaxValue;
            var key1AsUlong = (ulong)key1;

            var g1 = GrainFactory.GetGrain<ITestGrain>(key1);
            Assert.Equal(key1, g1.GetPrimaryKeyLong());
            Assert.Equal((long)key1AsUlong, g1.GetPrimaryKeyLong());
            Assert.Equal(key1, g1.GetKey().Result);
            Assert.Equal(key1.ToString(CultureInfo.InvariantCulture), g1.GetLabel().Result);

            g1.SetLabel("MaxValue").Wait();
            Assert.Equal("MaxValue", g1.GetLabel().Result);

            var g1a = GrainFactory.GetGrain<ITestGrain>((long)key1AsUlong);
            Assert.Equal("MaxValue", g1a.GetLabel().Result);
            Assert.Equal(key1, g1a.GetPrimaryKeyLong());
            Assert.Equal((long)key1AsUlong, g1a.GetKey().Result);
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public void BasicActivation_Long_MinValue()
        {
            var key1 = long.MinValue;
            var key1AsUlong = (ulong)key1;

            var g1 = GrainFactory.GetGrain<ITestGrain>(key1);
            Assert.Equal((long)key1AsUlong, g1.GetPrimaryKeyLong());
            Assert.Equal(key1, g1.GetPrimaryKeyLong());
            Assert.Equal(key1, g1.GetKey().Result);
            Assert.Equal(key1.ToString(CultureInfo.InvariantCulture), g1.GetLabel().Result);

            g1.SetLabel("MinValue").Wait();
            Assert.Equal("MinValue", g1.GetLabel().Result);

            var g1a = GrainFactory.GetGrain<ITestGrain>((long)key1AsUlong);
            Assert.Equal("MinValue", g1a.GetLabel().Result);
            Assert.Equal(key1, g1a.GetPrimaryKeyLong());
            Assert.Equal((long)key1AsUlong, g1a.GetKey().Result);
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public void BasicActivation_MultipleGrainInterfaces()
        {
            var simple = GrainFactory.GetGrain<ITestGrain>(GetRandomGrainId());

            simple.GetMultipleGrainInterfaces_List().Wait();
            Logger.LogInformation("GetMultipleGrainInterfaces_List() worked");

            simple.GetMultipleGrainInterfaces_Array().Wait();

            Logger.LogInformation("GetMultipleGrainInterfaces_Array() worked");
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("ActivateDeactivate"),
         TestCategory("Reentrancy")]
        public void BasicActivation_Reentrant_RecoveryAfterExpiredMessage()
        {
            var promises = new List<Task>();
            var prevTimeout = GetResponseTimeout();
            try
            {
                // set short response time and ask to do long operation, to trigger expired msgs in the silo queues.
                var shortTimeout = TimeSpan.FromMilliseconds(1000);
                SetResponseTimeout(shortTimeout);

                var grain = GrainFactory.GetGrain<ITestGrain>(GetRandomGrainId());
                var num = 10;
                for (long i = 0; i < num; i++)
                {
                    var task = grain.DoLongAction(
                        TimeSpan.FromMilliseconds(shortTimeout.TotalMilliseconds * 3),
                        "A_" + i);
                    promises.Add(task);
                }
                try
                {
                    Task.WhenAll(promises).Wait();
                }
                catch (Exception)
                {
                    Logger.LogInformation("Done with stress iteration.");
                }

                // wait a bit to make sure expired msgs in the silo is trigered.
                Thread.Sleep(TimeSpan.FromSeconds(10));

                // set the regular response time back, expect msgs ot succeed.
                SetResponseTimeout(prevTimeout);
                
                Logger.LogInformation("About to send a next legit request that should succeed.");
                grain.DoLongAction(TimeSpan.FromMilliseconds(1), "B_" + 0).Wait();
                Logger.LogInformation("The request succeeded.");
            }
            finally
            {
                // set the regular response time back, expect msgs ot succeed.
                SetResponseTimeout(prevTimeout);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("RequestContext"), TestCategory("GetGrain")]
        public void BasicActivation_TestRequestContext()
        {
            var g1 = GrainFactory.GetGrain<ITestGrain>(GetRandomGrainId());
            var promise1 = g1.TestRequestContext();
            var requestContext = promise1.Result;
            Logger.LogInformation("Request Context is: {RequestContext}", requestContext);
            Assert.NotNull(requestContext.Item2);
            Assert.NotNull(requestContext.Item1);
        }
    }
}

#pragma warning restore 618
