using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Tests fundamental grain activation and lifecycle behaviors in Orleans.
    /// These tests verify core functionality including:
    /// - Grain activation and identity management
    /// - State persistence across activations
    /// - Error handling during activation
    /// - Support for different key types (long, Guid, ulong)
    /// - Request context propagation
    /// Orleans uses an activation model where grains are activated on-demand and can be deactivated when idle.
    /// </summary>
    public class BasicActivationTests : HostedTestClusterEnsureDefaultStarted
    {
        public BasicActivationTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        private TimeSpan GetResponseTimeout() => this.Client.ServiceProvider.GetRequiredService<OutsideRuntimeClient>().GetResponseTimeout();
        private void SetResponseTimeout(TimeSpan value) => this.Client.ServiceProvider.GetRequiredService<OutsideRuntimeClient>().SetResponseTimeout(value);

        /// <summary>
        /// Tests basic grain activation with long keys and state updates.
        /// Verifies that:
        /// - Grains are properly activated when accessed
        /// - State changes persist within the same activation
        /// - Multiple grain references to the same identity share the same activation
        /// This demonstrates Orleans' virtual actor model where grains exist logically even when not activated.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public async Task BasicActivation_ActivateAndUpdate()
        {
            long g1Key = GetRandomGrainId();
            long g2Key = GetRandomGrainId();
            ITestGrain g1 = this.GrainFactory.GetGrain<ITestGrain>(g1Key);
            ITestGrain g2 = this.GrainFactory.GetGrain<ITestGrain>(g2Key);
            Assert.Equal(g1Key, g1.GetPrimaryKeyLong());
            Assert.Equal(g1Key, await g1.GetKey());
            Assert.Equal(g1Key.ToString(), await g1.GetLabel());
            Assert.Equal(g2Key, await g2.GetKey());
            Assert.Equal(g2Key.ToString(), await g2.GetLabel());

            await g1.SetLabel("one");
            Assert.Equal("one", await g1.GetLabel());
            Assert.Equal(g2Key.ToString(), await g2.GetLabel());

            ITestGrain g1a = this.GrainFactory.GetGrain<ITestGrain>(g1Key);
            Assert.Equal("one", await g1a.GetLabel());
        }

        /// <summary>
        /// Tests grain activation using Guid keys instead of long keys.
        /// Verifies that the grain system properly handles Guid-based grain identities.
        /// GUIDs are commonly used for grain keys when natural numeric identifiers don't exist.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public async Task BasicActivation_Guid_ActivateAndUpdate()
        {
            Guid guid1 = Guid.NewGuid();
            Guid guid2 = Guid.NewGuid();

            IGuidTestGrain g1 = this.GrainFactory.GetGrain<IGuidTestGrain>(guid1);
            IGuidTestGrain g2 = this.GrainFactory.GetGrain<IGuidTestGrain>(guid2);
            Assert.Equal(guid1, g1.GetPrimaryKey());
            Assert.Equal(guid1, await g1.GetKey());
            Assert.Equal(guid1.ToString(), await g1.GetLabel());
            Assert.Equal(guid2, await g2.GetKey());
            Assert.Equal(guid2.ToString(), await g2.GetLabel());

            await g1.SetLabel("one");
            Assert.Equal("one", await g1.GetLabel());
            Assert.Equal(guid2.ToString(), await g2.GetLabel());

            IGuidTestGrain g1a = this.GrainFactory.GetGrain<IGuidTestGrain>(guid1);
            Assert.Equal("one", await g1a.GetLabel());
        }

        /// <summary>
        /// Tests error handling when grain activation fails due to invalid parameters.
        /// Verifies that grains can enforce constraints during activation (e.g., rejecting certain key values).
        /// This demonstrates Orleans' ability to fail fast when grain invariants are violated.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate"), TestCategory("ErrorHandling"), TestCategory("GetGrain")]
        public async Task BasicActivation_Fail()
        {
            bool failed;
            long key = 0;
            try
            {
                // Key values of -2 are not allowed in this case
                ITestGrain fail = this.GrainFactory.GetGrain<ITestGrain>(-2);
                key = await fail.GetKey();
                failed = false;
            }
            catch (ArgumentException)
            {
                failed = true;
            }

            if (!failed) Assert.Fail("Should have failed, but instead returned " + key);
        }

        /// <summary>
        /// Tests error handling when multiple concurrent requests fail during grain activation.
        /// Verifies that Orleans properly handles burst failures without resource leaks or deadlocks.
        /// All concurrent requests should receive the same activation failure.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate"), TestCategory("ErrorHandling"), TestCategory("GetGrain")]
        public async Task BasicActivation_BurstFail()
        {
            bool failed;
            long key = 0;
            var tasks = new List<Task>();
            try
            {
                // Key values of -2 are not allowed in this case
                var fail = this.GrainFactory.GetGrain<ITestGrainLongOnActivateAsync>(-2);
                for (int i = 0; i < 10000; i++)
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

            if (!failed) Assert.Fail("Should have failed, but instead returned " + key);
        }

        /// <summary>
        /// Tests grain activation with ulong.MaxValue cast to long (results in -1).
        /// Verifies proper handling of edge cases in numeric grain keys.
        /// This ensures the grain system correctly handles the full range of long values.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public async Task BasicActivation_ULong_MaxValue()
        {
            ulong key1AsUlong = ulong.MaxValue; // == -1L
            long key1 = (long)key1AsUlong;

            ITestGrain g1 = this.GrainFactory.GetGrain<ITestGrain>(key1);
            Assert.Equal(key1, g1.GetPrimaryKeyLong());
            Assert.Equal((long)key1AsUlong, g1.GetPrimaryKeyLong());
            Assert.Equal(key1, await g1.GetKey());
            Assert.Equal(key1.ToString(CultureInfo.InvariantCulture), await g1.GetLabel());

            await g1.SetLabel("MaxValue");
            Assert.Equal("MaxValue", await g1.GetLabel());

            ITestGrain g1a = this.GrainFactory.GetGrain<ITestGrain>((long)key1AsUlong);
            Assert.Equal("MaxValue", await g1a.GetLabel());
            Assert.Equal(key1, g1a.GetPrimaryKeyLong());
            Assert.Equal((long)key1AsUlong, await g1a.GetKey());
        }

        /// <summary>
        /// Tests grain activation with ulong.MinValue (0) cast to long.
        /// Verifies proper handling of zero as a grain key.
        /// Zero is a valid grain key and should work like any other numeric identifier.
        /// </summary>
        [Fact, TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public async Task BasicActivation_ULong_MinValue()
        {
            ulong key1AsUlong = ulong.MinValue; // == zero
            long key1 = (long)key1AsUlong;

            ITestGrain g1 = this.GrainFactory.GetGrain<ITestGrain>(key1);
            Assert.Equal(key1, g1.GetPrimaryKeyLong());
            Assert.Equal((long)key1AsUlong, g1.GetPrimaryKeyLong());
            Assert.Equal(key1, g1.GetPrimaryKeyLong());
            Assert.Equal(key1, await g1.GetKey());
            Assert.Equal(key1.ToString(CultureInfo.InvariantCulture), await g1.GetLabel());

            await g1.SetLabel("MinValue");
            Assert.Equal("MinValue", await g1.GetLabel());

            ITestGrain g1a = this.GrainFactory.GetGrain<ITestGrain>((long)key1AsUlong);
            Assert.Equal("MinValue", await g1a.GetLabel());
            Assert.Equal(key1, g1a.GetPrimaryKeyLong());
            Assert.Equal((long)key1AsUlong, await g1a.GetKey());
        }

        /// <summary>
        /// Tests grain activation with int.MaxValue as the grain key.
        /// Verifies proper handling of large positive grain keys.
        /// This ensures no overflow or precision issues with large numeric identifiers.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public async Task BasicActivation_Long_MaxValue()
        {
            long key1 = int.MaxValue;
            ulong key1AsUlong = (ulong)key1;

            ITestGrain g1 = this.GrainFactory.GetGrain<ITestGrain>(key1);
            Assert.Equal(key1, g1.GetPrimaryKeyLong());
            Assert.Equal((long)key1AsUlong, g1.GetPrimaryKeyLong());
            Assert.Equal(key1, await g1.GetKey());
            Assert.Equal(key1.ToString(CultureInfo.InvariantCulture), await g1.GetLabel());

            await g1.SetLabel("MaxValue");
            Assert.Equal("MaxValue", await g1.GetLabel());

            ITestGrain g1a = this.GrainFactory.GetGrain<ITestGrain>((long)key1AsUlong);
            Assert.Equal("MaxValue", await g1a.GetLabel());
            Assert.Equal(key1, g1a.GetPrimaryKeyLong());
            Assert.Equal((long)key1AsUlong, await g1a.GetKey());
        }

        /// <summary>
        /// Tests grain activation with long.MinValue as the grain key.
        /// Verifies proper handling of the most negative possible grain key.
        /// This ensures the grain system handles the full range of signed long values.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public async Task BasicActivation_Long_MinValue()
        {
            long key1 = long.MinValue;
            ulong key1AsUlong = (ulong)key1;

            ITestGrain g1 = this.GrainFactory.GetGrain<ITestGrain>(key1);
            Assert.Equal((long)key1AsUlong, g1.GetPrimaryKeyLong());
            Assert.Equal(key1, g1.GetPrimaryKeyLong());
            Assert.Equal(key1, await g1.GetKey());
            Assert.Equal(key1.ToString(CultureInfo.InvariantCulture), await g1.GetLabel());

            await g1.SetLabel("MinValue");
            Assert.Equal("MinValue", await g1.GetLabel());

            ITestGrain g1a = this.GrainFactory.GetGrain<ITestGrain>((long)key1AsUlong);
            Assert.Equal("MinValue", await g1a.GetLabel());
            Assert.Equal(key1, g1a.GetPrimaryKeyLong());
            Assert.Equal((long)key1AsUlong, await g1a.GetKey());
        }

        /// <summary>
        /// Tests grain activation when grains implement multiple interfaces.
        /// Verifies that grains can return references to other grains through various collection types.
        /// This demonstrates Orleans' support for complex grain interface hierarchies.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task BasicActivation_MultipleGrainInterfaces()
        {
            ITestGrain simple = this.GrainFactory.GetGrain<ITestGrain>(GetRandomGrainId());

            await simple.GetMultipleGrainInterfaces_List();
            this.Logger.LogInformation("GetMultipleGrainInterfaces_List() worked");

            await simple.GetMultipleGrainInterfaces_Array();

            this.Logger.LogInformation("GetMultipleGrainInterfaces_Array() worked");
        }

        /// <summary>
        /// Tests recovery after message timeout in reentrant grains.
        /// Verifies that:
        /// - Grains can recover from expired messages in their queues
        /// - Subsequent valid requests succeed after timeouts
        /// - The system properly cleans up expired messages
        /// This tests Orleans' resilience to transient failures and timeout conditions.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("ActivateDeactivate"),
         TestCategory("Reentrancy")]
        public async Task BasicActivation_Reentrant_RecoveryAfterExpiredMessage()
        {
            List<Task> promises = new List<Task>();
            TimeSpan prevTimeout = this.GetResponseTimeout();
            try
            {
                // set short response time and ask to do long operation, to trigger expired msgs in the silo queues.
                TimeSpan shortTimeout = TimeSpan.FromMilliseconds(1000);
                this.SetResponseTimeout(shortTimeout);

                ITestGrain grain = this.GrainFactory.GetGrain<ITestGrain>(GetRandomGrainId());
                int num = 10;
                for (long i = 0; i < num; i++)
                {
                    Task task = grain.DoLongAction(
                        TimeSpan.FromMilliseconds(shortTimeout.TotalMilliseconds * 3),
                        "A_" + i);
                    promises.Add(task);
                }
                try
                {
                    await Task.WhenAll(promises);
                }
                catch (Exception)
                {
                    this.Logger.LogInformation("Done with stress iteration.");
                }

                // wait a bit to make sure expired msgs in the silo is trigered.
                Thread.Sleep(TimeSpan.FromSeconds(10));

                // set the regular response time back, expect msgs ot succeed.
                this.SetResponseTimeout(prevTimeout);
                
                this.Logger.LogInformation("About to send a next legit request that should succeed.");
                await grain.DoLongAction(TimeSpan.FromMilliseconds(1), "B_" + 0);
                this.Logger.LogInformation("The request succeeded.");
            }
            finally
            {
                // set the regular response time back, expect msgs ot succeed.
                this.SetResponseTimeout(prevTimeout);
            }
        }

        /// <summary>
        /// Tests request context propagation from client to grain.
        /// Verifies that Orleans properly flows ambient context (like trace IDs or user context)
        /// from the client through to grain method executions.
        /// Request context is essential for distributed tracing and multi-tenancy scenarios.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("RequestContext"), TestCategory("GetGrain")]
        public async Task BasicActivation_TestRequestContext()
        {
            ITestGrain g1 = this.GrainFactory.GetGrain<ITestGrain>(GetRandomGrainId());
            Task<Tuple<string, string>> promise1 = g1.TestRequestContext();
            Tuple<string, string> requestContext = await promise1;
            this.Logger.LogInformation("Request Context is: {RequestContext}", requestContext);
            Assert.NotNull(requestContext.Item2);
            Assert.NotNull(requestContext.Item1);
        }
    }
}
