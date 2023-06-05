using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    public class EchoTaskGrainTests : HostedTestClusterEnsureDefaultStarted
    {
        private readonly TimeSpan timeout = Debugger.IsAttached ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(10);

        const string expectedEcho = "Hello from EchoGrain";
        const string expectedEchoError = "Error from EchoGrain";
        private IEchoTaskGrain grain;

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

        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public void EchoGrain_GetGrain()
        {
            grain = GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
        }

        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_Echo()
        {
            var clock = new Stopwatch();

            clock.Start();
            grain = GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            Logger.LogInformation("CreateGrain took {Elapsed}", clock.Elapsed);

            clock.Restart();
            var received = await grain.EchoAsync(expectedEcho);
            Logger.LogInformation("EchoGrain.Echo took {Elapsed}", clock.Elapsed);

            Assert.Equal(expectedEcho, received);
        }

        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_EchoError()
        {
            grain = GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
        
            var promise = grain.EchoErrorAsync(expectedEchoError);
            await promise.ContinueWith(t =>
            {
                if (!t.IsFaulted) Assert.True(false); // EchoError should not have completed successfully

                Exception exc = t.Exception;
                while (exc is AggregateException) exc = exc.InnerException;
                var received = exc.Message;
                Assert.Equal(expectedEchoError, received);
            }).WithTimeout(timeout);
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Echo"), TestCategory("Timeout")]
        public async Task EchoGrain_Timeout_Wait()
        {
            grain = GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
        
            var delay30 = TimeSpan.FromSeconds(30); // grain call timeout (set in config)
            var delay45 = TimeSpan.FromSeconds(45);
            var delay60 = TimeSpan.FromSeconds(60);
            var sw = new Stopwatch();
            sw.Start();
            var promise = grain.BlockingCallTimeoutAsync(delay60);
            await promise.ContinueWith(
                t =>
                {
                    if (!t.IsFaulted) Assert.True(false); // BlockingCallTimeout should not have completed successfully

                    Exception exc = t.Exception;
                    while (exc is AggregateException) exc = exc.InnerException;
                    Assert.IsAssignableFrom<TimeoutException>(exc);
                }).WithTimeout(delay45);
            sw.Stop();
            Assert.True(TimeIsLonger(sw.Elapsed, delay30), $"Elapsed time out of range: {sw.Elapsed}");
            Assert.True(TimeIsShorter(sw.Elapsed, delay60), $"Elapsed time out of range: {sw.Elapsed}");
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Echo")]
        public async Task EchoGrain_Timeout_Await()
        {
            grain = GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            
            var delay30 = TimeSpan.FromSeconds(30);
            var delay60 = TimeSpan.FromSeconds(60);
            var sw = new Stopwatch();
            sw.Start();
            try
            {
                var res = await grain.BlockingCallTimeoutAsync(delay60);
                Assert.True(false); // BlockingCallTimeout should not have completed successfully
            }
            catch (Exception exc)
            {
                while (exc is AggregateException) exc = exc.InnerException;
                Assert.IsAssignableFrom<TimeoutException>(exc);
            }
            sw.Stop();
            Assert.True(TimeIsLonger(sw.Elapsed, delay30), $"Elapsed time out of range: {sw.Elapsed}");
            Assert.True(TimeIsShorter(sw.Elapsed, delay60), $"Elapsed time out of range: {sw.Elapsed}");
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Echo"), TestCategory("Timeout")]
        public async Task EchoGrain_Timeout_Result()
        {
            grain = GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            
            var delay30 = TimeSpan.FromSeconds(30);
            var delay60 = TimeSpan.FromSeconds(60);
            var sw = new Stopwatch();
            sw.Start();
            try
            {
                var res = await grain.BlockingCallTimeoutAsync(delay60);
                Assert.True(false, "BlockingCallTimeout should not have completed successfully, but returned " + res);
            }
            catch (Exception exc)
            {
                while (exc is AggregateException) exc = exc.InnerException;
                Assert.IsAssignableFrom<TimeoutException>(exc);
            }
            sw.Stop();
            Assert.True(TimeIsLonger(sw.Elapsed, delay30), $"Elapsed time out of range: {sw.Elapsed}");
            Assert.True(TimeIsShorter(sw.Elapsed, delay60), $"Elapsed time out of range: {sw.Elapsed}");
        }

        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_LastEcho()
        {
            var clock = new Stopwatch();

            await EchoGrain_Echo();

            clock.Start();
            var received = await grain.GetLastEchoAsync();
            Logger.LogInformation("EchoGrain.LastEcho took {Elapsed}", clock.Elapsed);

            Assert.Equal(expectedEcho, received); // LastEcho-Echo

            await EchoGrain_EchoError();

            clock.Restart();
            received = await grain.GetLastEchoAsync();
            Logger.LogInformation("EchoGrain.LastEcho-Error took {Elapsed}", clock.Elapsed);

            Assert.Equal(expectedEchoError, received); // LastEcho-Error
        }

        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_Ping()
        {
            var clock = new Stopwatch();

            var what = "CreateGrain";
            clock.Start();
            grain = GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);

            what = "EchoGrain.Ping";
            clock.Restart();
            
            await grain.PingAsync().WithTimeout(timeout);
            Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);
        }

        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_PingSilo_Local()
        {
            var clock = new Stopwatch();

            var what = "CreateGrain";
            clock.Start();
            grain = GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);

            what = "EchoGrain.PingLocalSilo";
            clock.Restart();
            await grain.PingLocalSiloAsync().WithTimeout(timeout);
            Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);
        }

        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_PingSilo_Remote()
        {
            var clock = new Stopwatch();

            var what = "CreateGrain";
            clock.Start();
            grain = GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);

            var silo1 = HostedCluster.Primary.SiloAddress;
            var silo2 = HostedCluster.SecondarySilos[0].SiloAddress;

            what = "EchoGrain.PingRemoteSilo[1]";
            clock.Restart();
            await grain.PingRemoteSiloAsync(silo1).WithTimeout(timeout);
            Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);

            what = "EchoGrain.PingRemoteSilo[2]";
            clock.Restart();
            await grain.PingRemoteSiloAsync(silo2).WithTimeout(timeout);
            Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);
        }

        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_PingSilo_OtherSilo()
        {
            var clock = new Stopwatch();

            var what = "CreateGrain";
            clock.Start();
            grain = GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);

            what = "EchoGrain.PingOtherSilo";
            clock.Restart();
            await grain.PingOtherSiloAsync().WithTimeout(timeout);
            Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);
        }

        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_PingSilo_OtherSilo_Membership()
        {
            var clock = new Stopwatch();

            var what = "CreateGrain";
            clock.Start();
            grain = GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);

            what = "EchoGrain.PingOtherSiloMembership";
            clock.Restart();
            await grain.PingClusterMemberAsync().WithTimeout(timeout);
            Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);
        }

        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoTaskGrain_Await()
        {
            var g = GrainFactory.GetGrain<IBlockingEchoTaskGrain>(GetRandomGrainId());

            var received = await g.Echo(expectedEcho);
            Assert.Equal(expectedEcho, received); // Echo

            received = await g.CallMethodAV_Await(expectedEcho);
            Assert.Equal(expectedEcho, received); // CallMethodAV_Await

            received = await g.CallMethodTask_Await(expectedEcho);
            Assert.Equal(expectedEcho, received); // CallMethodTask_Await
        }

        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoTaskGrain_Await_Reentrant()
        {
            var g = GrainFactory.GetGrain<IReentrantBlockingEchoTaskGrain>(GetRandomGrainId());

            var received = await g.Echo(expectedEcho);
            Assert.Equal(expectedEcho, received); // Echo

            received = await g.CallMethodAV_Await(expectedEcho);
            Assert.Equal(expectedEcho, received); // CallMethodAV_Await

            received = await g.CallMethodTask_Await(expectedEcho);
            Assert.Equal(expectedEcho, received); // CallMethodTask_Await
        }

        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_EchoNullable()
        {
            var clock = new Stopwatch();

            clock.Start();
            var grain = GrainFactory.GetGrain<IEchoGrain>(Guid.NewGuid());
            Logger.LogInformation("CreateGrain took {Elapsed}", clock.Elapsed);

            clock.Restart();
            var now = DateTime.Now;
            var received = await grain.EchoNullable(now);
            Logger.LogInformation("EchoGrain.EchoNullable took {Elapsed}", clock.Elapsed);

            Assert.Equal(now, received);

            clock.Restart();
            received = await grain.EchoNullable(null);
            Logger.LogInformation("EchoGrain.EchoNullable took {Elapsed}", clock.Elapsed);
            Assert.Null(received);
        }

        // ---------- Utility methods ----------

        private bool TimeIsLonger(TimeSpan time, TimeSpan limit)
        {
            Logger.LogInformation("Compare TimeIsLonger: Actual={Time} Limit={Limit} Epsilon={Epsilon}", time, limit, Epsilon);
            return time >= (limit - Epsilon);
        }

        private bool TimeIsShorter(TimeSpan time, TimeSpan limit)
        {
            Logger.LogInformation("Compare TimeIsShorter: Actual={Time} Limit={Limit} Epsilon={Epsilon}", time, limit, Epsilon);
            return time <= (limit + Epsilon);
        }
    }
}
