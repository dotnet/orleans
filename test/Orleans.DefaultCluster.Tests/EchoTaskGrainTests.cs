using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Tests basic grain communication patterns using Echo grains.
    /// Echo grains are simple test grains that return or process the input they receive.
    /// These tests verify fundamental Orleans features:
    /// - Basic request/response communication
    /// - Error propagation from grains to clients
    /// - Timeout handling and recovery
    /// - Cross-silo communication
    /// - Async/await patterns in grain methods
    /// - Nullable type handling across grain boundaries
    /// </summary>
    public class EchoTaskGrainTests : HostedTestClusterEnsureDefaultStarted
    {
        private readonly TimeSpan timeout = Debugger.IsAttached ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(10);
        private const string expectedEcho = "Hello from EchoGrain";
        private const string expectedEchoError = "Error from EchoGrain";

        public static readonly TimeSpan Epsilon = TimeSpan.FromSeconds(1);

        public EchoTaskGrainTests(DefaultClusterFixture fixture)
            : base(fixture)
        {
            if (HostedCluster.SecondarySilos.Count == 0)
            {
                HostedCluster.StartAdditionalSilo();
                HostedCluster.WaitForLivenessToStabilizeAsync().Wait();
            }
        }

        /// <summary>
        /// Tests basic grain reference creation.
        /// Verifies that grain references can be obtained without activation.
        /// Getting a grain reference is a local operation that doesn't communicate with the cluster.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public void EchoGrain_GetGrain()
        {
            _ = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
        }

        /// <summary>
        /// Tests basic grain method invocation with string echo.
        /// Verifies that:
        /// - Grain activation occurs on first call
        /// - Method parameters are correctly marshaled to the grain
        /// - Return values are correctly marshaled back to the client
        /// This is the most fundamental test of grain communication.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_Echo()
        {
            Stopwatch clock = new Stopwatch();

            clock.Start();
            var grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            this.Logger.LogInformation("CreateGrain took {Elapsed}", clock.Elapsed);

            clock.Restart();
            string received = await grain.EchoAsync(expectedEcho);
            this.Logger.LogInformation("EchoGrain.Echo took {Elapsed}", clock.Elapsed);

            Assert.Equal(expectedEcho, received);
        }

        /// <summary>
        /// Tests exception propagation from grain to client.
        /// Verifies that exceptions thrown in grain methods are:
        /// - Serialized and sent back to the client
        /// - Wrapped in appropriate exception types
        /// - Contain the original error message
        /// This ensures proper error handling in distributed scenarios.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_EchoError()
        {
            var grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());

            Task<string> promise = grain.EchoErrorAsync(expectedEchoError);
            await promise.ContinueWith(t =>
            {
                if (!t.IsFaulted) Assert.True(false); // EchoError should not have completed successfully

                Exception exc = t.Exception;
                while (exc is AggregateException) exc = exc.InnerException;
                string received = exc.Message;
                Assert.Equal(expectedEchoError, received);
            }).WaitAsync(timeout);
        }

        /// <summary>
        /// Tests timeout handling using ContinueWith pattern.
        /// Verifies that:
        /// - Long-running grain methods timeout according to configuration
        /// - TimeoutException is properly thrown
        /// - Timeout occurs within expected time bounds
        /// This tests Orleans' ability to prevent hung calls from blocking indefinitely.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Echo"), TestCategory("Timeout")]
        public async Task EchoGrain_Timeout_ContinueWith()
        {
            var grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());

            TimeSpan delay5 = TimeSpan.FromSeconds(30); // grain call timeout (set in config)
            TimeSpan delay45 = TimeSpan.FromSeconds(45);
            TimeSpan delay60 = TimeSpan.FromSeconds(60);
            Stopwatch sw = new Stopwatch();
            sw.Start();
            Task<int> promise = grain.BlockingCallTimeoutNoResponseTimeoutOverrideAsync(delay60);
            await promise.ContinueWith(
                t =>
                {
                    if (!t.IsFaulted) Assert.Fail("BlockingCallTimeout should not have completed successfully");

                    Exception exc = t.Exception;
                    while (exc is AggregateException) exc = exc.InnerException;
                    Assert.IsAssignableFrom<TimeoutException>(exc);
                }).WaitAsync(delay45);
            sw.Stop();
            Assert.True(TimeIsLonger(sw.Elapsed, delay5), $"Elapsed time out of range: {sw.Elapsed}");
            Assert.True(TimeIsShorter(sw.Elapsed, delay60), $"Elapsed time out of range: {sw.Elapsed}");
        }

        /// <summary>
        /// Tests timeout handling using async/await pattern.
        /// Similar to Timeout_ContinueWith but using modern async/await syntax.
        /// Verifies proper timeout behavior with await-based error handling.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Echo")]
        public async Task EchoGrain_Timeout_Await()
        {
            var grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());

            TimeSpan delay5 = TimeSpan.FromSeconds(5);
            TimeSpan delay25 = TimeSpan.FromSeconds(25);
            Stopwatch sw = new Stopwatch();
            sw.Start();
            try
            {
                int res = await grain.BlockingCallTimeoutAsync(delay25);
                Assert.Fail($"BlockingCallTimeout should not have completed successfully, but returned {res}");
            }
            catch (Exception exc)
            {
                while (exc is AggregateException) exc = exc.InnerException;
                Assert.IsAssignableFrom<TimeoutException>(exc);
            }
            sw.Stop();
            Assert.True(TimeIsLonger(sw.Elapsed, delay5), $"Elapsed time out of range: {sw.Elapsed}");
            Assert.True(TimeIsShorter(sw.Elapsed, delay25), $"Elapsed time out of range: {sw.Elapsed}");
        }

        /// <summary>
        /// Tests timeout handling when using Task.Result (blocking wait).
        /// Verifies that timeouts work correctly even when using synchronous waiting patterns.
        /// Note: This pattern is generally discouraged but needs to work for compatibility.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Echo"), TestCategory("Timeout")]
        public async Task EchoGrain_Timeout_Result()
        {
            var grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());

            TimeSpan delay5 = TimeSpan.FromSeconds(5);
            TimeSpan delay25 = TimeSpan.FromSeconds(25);
            Stopwatch sw = new Stopwatch();
            sw.Start();
            try
            {
                // Note that this method purposely uses Task.Result.
                int res = await Task.Run(() =>
                {
#pragma warning disable xUnit1031 // Do not use blocking task operations in test method
                    return grain.BlockingCallTimeoutAsync(delay25).Result;
#pragma warning restore xUnit1031 // Do not use blocking task operations in test method
                });

                Assert.Fail($"BlockingCallTimeout should not have completed successfully, but returned {res}");
            }
            catch (Exception exc)
            {
                while (exc is AggregateException) exc = exc.InnerException;
                Assert.IsAssignableFrom<TimeoutException>(exc);
            }
            sw.Stop();
            Assert.True(TimeIsLonger(sw.Elapsed, delay5), $"Elapsed time out of range: {sw.Elapsed}");
            Assert.True(TimeIsShorter(sw.Elapsed, delay25), $"Elapsed time out of range: {sw.Elapsed}");
        }

        /// <summary>
        /// Tests grain state persistence across multiple calls.
        /// Verifies that:
        /// - Grains can maintain state between calls
        /// - State is updated correctly for both successful and error cases
        /// - The last value (or error message) is retrievable
        /// This demonstrates basic stateful grain behavior.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_LastEcho()
        {
            Stopwatch clock = new Stopwatch();

            clock.Start();
            var grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            this.Logger.LogInformation("CreateGrain took {Elapsed}", clock.Elapsed);

            clock.Restart();
            string received = await grain.EchoAsync(expectedEcho);
            this.Logger.LogInformation("EchoGrain.Echo took {Elapsed}", clock.Elapsed);

            Assert.Equal(expectedEcho, received);

            clock.Start();

            received = await grain.GetLastEchoAsync();
            this.Logger.LogInformation("EchoGrain.LastEcho took {Elapsed}", clock.Elapsed);

            Assert.Equal(expectedEcho, received); // LastEcho-Echo

            Task<string> promise = grain.EchoErrorAsync(expectedEchoError);
            await promise.ContinueWith(t =>
            {
                if (!t.IsFaulted) Assert.True(false); // EchoError should not have completed successfully

                Exception exc = t.Exception;
                while (exc is AggregateException) exc = exc.InnerException;
                string received = exc.Message;
                Assert.Equal(expectedEchoError, received);
            }).WaitAsync(timeout);

            clock.Restart();
            received = await grain.GetLastEchoAsync();
            this.Logger.LogInformation("EchoGrain.LastEcho-Error took {Elapsed}", clock.Elapsed);

            Assert.Equal(expectedEchoError, received); // LastEcho-Error
        }

        /// <summary>
        /// Tests basic grain liveness check (ping).
        /// Verifies that grains can respond to simple parameterless calls.
        /// Ping operations are useful for health checks and keeping grains activated.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_Ping()
        {
            Stopwatch clock = new Stopwatch();

            string what = "CreateGrain";
            clock.Start();
            var grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);

            what = "EchoGrain.Ping";
            clock.Restart();

            await grain.PingAsync().WaitAsync(timeout);
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);
        }

        /// <summary>
        /// Tests grain's ability to ping its local silo.
        /// Verifies intra-silo communication where a grain communicates with its hosting silo.
        /// This pattern is useful for silo-level health checks and diagnostics.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_PingSilo_Local()
        {
            Stopwatch clock = new Stopwatch();

            string what = "CreateGrain";
            clock.Start();
            var grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);

            what = "EchoGrain.PingLocalSilo";
            clock.Restart();
            await grain.PingLocalSiloAsync().WaitAsync(timeout);
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);
        }

        /// <summary>
        /// Tests grain's ability to ping specific remote silos.
        /// Verifies cross-silo communication where a grain can target specific silos.
        /// This demonstrates Orleans' ability to route messages to specific cluster nodes.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_PingSilo_Remote()
        {
            Stopwatch clock = new Stopwatch();

            string what = "CreateGrain";
            clock.Start();
            var grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);

            SiloAddress silo1 = HostedCluster.Primary.SiloAddress;
            SiloAddress silo2 = HostedCluster.SecondarySilos[0].SiloAddress;

            what = "EchoGrain.PingRemoteSilo[1]";
            clock.Restart();
            await grain.PingRemoteSiloAsync(silo1).WaitAsync(timeout);
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);

            what = "EchoGrain.PingRemoteSilo[2]";
            clock.Restart();
            await grain.PingRemoteSiloAsync(silo2).WaitAsync(timeout);
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);
        }

        /// <summary>
        /// Tests grain's ability to ping any other silo in the cluster.
        /// Verifies that grains can discover and communicate with other silos dynamically.
        /// This is useful for distributed health checks and cluster topology awareness.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_PingSilo_OtherSilo()
        {
            Stopwatch clock = new Stopwatch();

            string what = "CreateGrain";
            clock.Start();
            var grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);

            what = "EchoGrain.PingOtherSilo";
            clock.Restart();
            await grain.PingOtherSiloAsync().WaitAsync(timeout);
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);
        }

        /// <summary>
        /// Tests grain's ability to interact with cluster membership.
        /// Verifies that grains can query and use membership information to communicate
        /// with other cluster members. This demonstrates Orleans' membership awareness.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_PingSilo_OtherSilo_Membership()
        {
            Stopwatch clock = new Stopwatch();

            string what = "CreateGrain";
            clock.Start();
            var grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);

            what = "EchoGrain.PingOtherSiloMembership";
            clock.Restart();
            await grain.PingClusterMemberAsync().WaitAsync(timeout);
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);
        }

        /// <summary>
        /// Tests various async/await patterns in non-reentrant grains.
        /// Verifies that blocking grains correctly handle:
        /// - Direct async method calls
        /// - Chained async calls (grain calling grain)
        /// - Different Task-based return patterns
        /// Non-reentrant grains process one message at a time.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoTaskGrain_Await()
        {
            IBlockingEchoTaskGrain g = this.GrainFactory.GetGrain<IBlockingEchoTaskGrain>(GetRandomGrainId());

            string received = await g.Echo(expectedEcho);
            Assert.Equal(expectedEcho, received); // Echo

            received = await g.CallMethodAV_Await(expectedEcho);
            Assert.Equal(expectedEcho, received); // CallMethodAV_Await

            received = await g.CallMethodTask_Await(expectedEcho);
            Assert.Equal(expectedEcho, received); // CallMethodTask_Await
        }

        /// <summary>
        /// Tests various async/await patterns in reentrant grains.
        /// Similar to EchoTaskGrain_Await but with reentrant grains that can process
        /// multiple messages concurrently. This verifies Orleans' reentrancy support.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoTaskGrain_Await_Reentrant()
        {
            IReentrantBlockingEchoTaskGrain g = this.GrainFactory.GetGrain<IReentrantBlockingEchoTaskGrain>(GetRandomGrainId());

            string received = await g.Echo(expectedEcho);
            Assert.Equal(expectedEcho, received); // Echo

            received = await g.CallMethodAV_Await(expectedEcho);
            Assert.Equal(expectedEcho, received); // CallMethodAV_Await

            received = await g.CallMethodTask_Await(expectedEcho);
            Assert.Equal(expectedEcho, received); // CallMethodTask_Await
        }

        /// <summary>
        /// Tests nullable type handling across grain boundaries.
        /// Verifies that:
        /// - Nullable values are correctly serialized when non-null
        /// - Null values are properly handled and returned as null
        /// This ensures Orleans correctly handles .NET nullable value types.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_EchoNullable()
        {
            Stopwatch clock = new Stopwatch();

            clock.Start();
            var grain = this.GrainFactory.GetGrain<IEchoGrain>(Guid.NewGuid());
            this.Logger.LogInformation("CreateGrain took {Elapsed}", clock.Elapsed);

            clock.Restart();
            var now = DateTime.Now;
            var received = await grain.EchoNullable(now);
            this.Logger.LogInformation("EchoGrain.EchoNullable took {Elapsed}", clock.Elapsed);

            Assert.Equal(now, received);

            clock.Restart();
            received = await grain.EchoNullable(null);
            this.Logger.LogInformation("EchoGrain.EchoNullable took {Elapsed}", clock.Elapsed);
            Assert.Null(received);
        }

        // ---------- Utility methods ----------

        private bool TimeIsLonger(TimeSpan time, TimeSpan limit)
        {
            this.Logger.LogInformation("Compare TimeIsLonger: Actual={Time} Limit={Limit} Epsilon={Epsilon}", time, limit, Epsilon);
            return time >= (limit - Epsilon);
        }

        private bool TimeIsShorter(TimeSpan time, TimeSpan limit)
        {
            this.Logger.LogInformation("Compare TimeIsShorter: Actual={Time} Limit={Limit} Epsilon={Epsilon}", time, limit, Epsilon);
            return time <= (limit + Epsilon);
        }
    }
}
