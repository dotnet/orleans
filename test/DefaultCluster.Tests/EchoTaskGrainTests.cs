using System;
using System.Diagnostics;
using System.Threading.Tasks;
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
            grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
        }

        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_Echo()
        {
            Stopwatch clock = new Stopwatch();

            clock.Start();
            grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            this.Logger.LogInformation("CreateGrain took {Elapsed}", clock.Elapsed);

            clock.Restart();
            string received = await grain.EchoAsync(expectedEcho);
            this.Logger.LogInformation("EchoGrain.Echo took {Elapsed}", clock.Elapsed);

            Assert.Equal(expectedEcho, received);
        }

        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_EchoError()
        {
            grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
        
            Task<string> promise = grain.EchoErrorAsync(expectedEchoError);
            await promise.ContinueWith(t =>
            {
                if (!t.IsFaulted) Assert.True(false); // EchoError should not have completed successfully

                Exception exc = t.Exception;
                while (exc is AggregateException) exc = exc.InnerException;
                string received = exc.Message;
                Assert.Equal(expectedEchoError, received);
            }).WithTimeout(timeout);
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Echo"), TestCategory("Timeout")]
        public async Task EchoGrain_Timeout_Wait()
        {
            grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
        
            TimeSpan delay30 = TimeSpan.FromSeconds(30); // grain call timeout (set in config)
            TimeSpan delay45 = TimeSpan.FromSeconds(45);
            TimeSpan delay60 = TimeSpan.FromSeconds(60);
            Stopwatch sw = new Stopwatch();
            sw.Start();
            Task<int> promise = grain.BlockingCallTimeoutAsync(delay60);
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
            grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            
            TimeSpan delay30 = TimeSpan.FromSeconds(30);
            TimeSpan delay60 = TimeSpan.FromSeconds(60);
            Stopwatch sw = new Stopwatch();
            sw.Start();
            try
            {
                int res = await grain.BlockingCallTimeoutAsync(delay60);
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
            grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            
            TimeSpan delay30 = TimeSpan.FromSeconds(30);
            TimeSpan delay60 = TimeSpan.FromSeconds(60);
            Stopwatch sw = new Stopwatch();
            sw.Start();
            try
            {
                int res = await grain.BlockingCallTimeoutAsync(delay60);
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
            Stopwatch clock = new Stopwatch();

            await EchoGrain_Echo();

            clock.Start();
            string received = await grain.GetLastEchoAsync();
            this.Logger.LogInformation("EchoGrain.LastEcho took {Elapsed}", clock.Elapsed);

            Assert.Equal(expectedEcho, received); // LastEcho-Echo

            await EchoGrain_EchoError();

            clock.Restart();
            received = await grain.GetLastEchoAsync();
            this.Logger.LogInformation("EchoGrain.LastEcho-Error took {Elapsed}", clock.Elapsed);

            Assert.Equal(expectedEchoError, received); // LastEcho-Error
        }

        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_Ping()
        {
            Stopwatch clock = new Stopwatch();

            string what = "CreateGrain";
            clock.Start();
            grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);

            what = "EchoGrain.Ping";
            clock.Restart();
            
            await grain.PingAsync().WithTimeout(timeout);
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);
        }

        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_PingSilo_Local()
        {
            Stopwatch clock = new Stopwatch();

            string what = "CreateGrain";
            clock.Start();
            grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);

            what = "EchoGrain.PingLocalSilo";
            clock.Restart();
            await grain.PingLocalSiloAsync().WithTimeout(timeout);
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);
        }

        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_PingSilo_Remote()
        {
            Stopwatch clock = new Stopwatch();

            string what = "CreateGrain";
            clock.Start();
            grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);

            SiloAddress silo1 = HostedCluster.Primary.SiloAddress;
            SiloAddress silo2 = HostedCluster.SecondarySilos[0].SiloAddress;

            what = "EchoGrain.PingRemoteSilo[1]";
            clock.Restart();
            await grain.PingRemoteSiloAsync(silo1).WithTimeout(timeout);
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);

            what = "EchoGrain.PingRemoteSilo[2]";
            clock.Restart();
            await grain.PingRemoteSiloAsync(silo2).WithTimeout(timeout);
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);
        }

        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_PingSilo_OtherSilo()
        {
            Stopwatch clock = new Stopwatch();

            string what = "CreateGrain";
            clock.Start();
            grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);

            what = "EchoGrain.PingOtherSilo";
            clock.Restart();
            await grain.PingOtherSiloAsync().WithTimeout(timeout);
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);
        }

        [Fact, TestCategory("BVT"), TestCategory("Echo")]
        public async Task EchoGrain_PingSilo_OtherSilo_Membership()
        {
            Stopwatch clock = new Stopwatch();

            string what = "CreateGrain";
            clock.Start();
            grain = this.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);

            what = "EchoGrain.PingOtherSiloMembership";
            clock.Restart();
            await grain.PingClusterMemberAsync().WithTimeout(timeout);
            this.Logger.LogInformation("{What} took {Elapsed}", what, clock.Elapsed);
        }

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
