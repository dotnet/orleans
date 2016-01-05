using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

namespace UnitTests.General
{
    [TestClass]
    public class EchoTaskGrainTests : UnitTestSiloHost
    {
        private readonly TimeSpan timeout = Debugger.IsAttached ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(10);

        const string expectedEcho = "Hello from EchoGrain";
        const string expectedEchoError = "Error from EchoGrain";
        private IEchoTaskGrain grain;

        public static readonly TimeSpan Epsilon = TimeSpan.FromSeconds(1);

        [ClassCleanup]
        public static void ClassCleanup()
        {
            StopAllSilos();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Echo")]
        public void EchoGrain_GetGrain()
        {
            grain = GrainClient.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Echo")]
        public async Task EchoGrain_Echo()
        {
            Stopwatch clock = new Stopwatch();

            clock.Start();
            grain = GrainClient.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            logger.Info("CreateGrain took " + clock.Elapsed);

            clock.Restart();
            string received = await grain.EchoAsync(expectedEcho);
            logger.Info("EchoGrain.Echo took " + clock.Elapsed);

            Assert.AreEqual(expectedEcho, received);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Echo")]
        public void EchoGrain_EchoError()
        {
            grain = GrainClient.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
        
            Task<string> promise = grain.EchoErrorAsync(expectedEchoError);
            bool ok = promise.ContinueWith(t =>
            {
                if (!t.IsFaulted) Assert.Fail("EchoError should not have completed successfully");

                Exception exc = t.Exception;
                while (exc is AggregateException) exc = exc.InnerException;
                string received = exc.Message;
                Assert.AreEqual(expectedEchoError, received);
            }).Wait(timeout);
            Assert.IsTrue(ok, "Finished OK");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Echo"), TestCategory("Timeout")]
        public void EchoGrain_Timeout_Wait()
        {
            grain = GrainClient.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
        
            TimeSpan delay30 = TimeSpan.FromSeconds(30); // grain call timeout (set in config)
            TimeSpan delay45 = TimeSpan.FromSeconds(45);
            TimeSpan delay60 = TimeSpan.FromSeconds(60);
            Stopwatch sw = new Stopwatch();
            sw.Start();
            Task<int> promise = grain.BlockingCallTimeoutAsync(delay60);
            bool ok = promise.ContinueWith(t =>
            {
                if (!t.IsFaulted) Assert.Fail("BlockingCallTimeout should not have completed successfully");

                Exception exc = t.Exception;
                while (exc is AggregateException) exc = exc.InnerException;
                Assert.IsInstanceOfType(exc, typeof(TimeoutException), "Received exception type: {0}", exc);
            }).Wait(delay45);
            sw.Stop();
            Assert.IsTrue(ok, "Wait should not have timed-out. The grain call should have time out.");
            Assert.IsTrue(TimeIsLonger(sw.Elapsed, delay30), "Elapsted time out of range: {0}", sw.Elapsed);
            Assert.IsTrue(TimeIsShorter(sw.Elapsed, delay60), "Elapsted time out of range: {0}", sw.Elapsed);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Echo")]
        public async Task EchoGrain_Timeout_Await()
        {
            grain = GrainClient.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            
            TimeSpan delay30 = TimeSpan.FromSeconds(30);
            TimeSpan delay60 = TimeSpan.FromSeconds(60);
            Stopwatch sw = new Stopwatch();
            sw.Start();
            try
            {
                int res = await grain.BlockingCallTimeoutAsync(delay60);
                Assert.Fail("BlockingCallTimeout should not have completed successfully");
            }
            catch (Exception exc)
            {
                while (exc is AggregateException) exc = exc.InnerException;
                Assert.IsInstanceOfType(exc, typeof(TimeoutException), "Received exception type: {0}", exc);
            }
            sw.Stop();
            Assert.IsTrue(TimeIsLonger(sw.Elapsed, delay30), "Elapsted time out of range: {0}", sw.Elapsed);
            Assert.IsTrue(TimeIsShorter(sw.Elapsed, delay60), "Elapsted time out of range: {0}", sw.Elapsed);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Echo"), TestCategory("Timeout")]
        public async Task EchoGrain_Timeout_Result()
        {
            grain = GrainClient.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            
            TimeSpan delay30 = TimeSpan.FromSeconds(30);
            TimeSpan delay60 = TimeSpan.FromSeconds(60);
            Stopwatch sw = new Stopwatch();
            sw.Start();
            try
            {
                int res = await grain.BlockingCallTimeoutAsync(delay60);
                Assert.Fail("BlockingCallTimeout should not have completed successfully, but returned " + res);
            }
            catch (Exception exc)
            {
                while (exc is AggregateException) exc = exc.InnerException;
                Assert.IsInstanceOfType(exc, typeof(TimeoutException), "Received exception type: {0}", exc);
            }
            sw.Stop();
            Assert.IsTrue(TimeIsLonger(sw.Elapsed, delay30), "Elapsted time out of range: {0}", sw.Elapsed);
            Assert.IsTrue(TimeIsShorter(sw.Elapsed, delay60), "Elapsted time out of range: {0}", sw.Elapsed);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Echo")]
        public async Task EchoGrain_LastEcho()
        {
            Stopwatch clock = new Stopwatch();

            await EchoGrain_Echo();

            clock.Start();
            string received = await grain.GetLastEchoAsync();
            logger.Info("EchoGrain.LastEcho took " + clock.Elapsed);

            Assert.AreEqual(expectedEcho, received, "LastEcho-Echo");

            EchoGrain_EchoError();

            clock.Restart();
            received = await grain.GetLastEchoAsync();
            logger.Info("EchoGrain.LastEcho-Error took " + clock.Elapsed);

            Assert.AreEqual(expectedEchoError, received, "LastEcho-Error");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Echo")]
        public async Task EchoGrain_Ping()
        {
            Stopwatch clock = new Stopwatch();

            string what = "CreateGrain";
            clock.Start();
            grain = GrainClient.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            logger.Info("{0} took {1}", what, clock.Elapsed);

            what = "EchoGrain.Ping";
            clock.Restart();
            
            await grain.PingAsync().WithTimeout(timeout);
            logger.Info("{0} took {1}", what, clock.Elapsed);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Echo")]
        public async Task EchoGrain_PingSilo_Local()
        {
            Stopwatch clock = new Stopwatch();

            string what = "CreateGrain";
            clock.Start();
            grain = GrainClient.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            logger.Info("{0} took {1}", what, clock.Elapsed);

            what = "EchoGrain.PingLocalSilo";
            clock.Restart();
            await grain.PingLocalSiloAsync().WithTimeout(timeout);
            logger.Info("{0} took {1}", what, clock.Elapsed);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Echo")]
        public async Task EchoGrain_PingSilo_Remote()
        {
            Stopwatch clock = new Stopwatch();

            string what = "CreateGrain";
            clock.Start();
            grain = GrainClient.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            logger.Info("{0} took {1}", what, clock.Elapsed);

            SiloAddress silo1 = Primary.Silo.SiloAddress;
            SiloAddress silo2 = Secondary.Silo.SiloAddress;

            what = "EchoGrain.PingRemoteSilo[1]";
            clock.Restart();
            await grain.PingRemoteSiloAsync(silo1).WithTimeout(timeout);
            logger.Info("{0} took {1}", what, clock.Elapsed);

            what = "EchoGrain.PingRemoteSilo[2]";
            clock.Restart();
            await grain.PingRemoteSiloAsync(silo2).WithTimeout(timeout);
            logger.Info("{0} took {1}", what, clock.Elapsed);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Echo")]
        public async Task EchoGrain_PingSilo_OtherSilo()
        {
            Stopwatch clock = new Stopwatch();

            string what = "CreateGrain";
            clock.Start();
            grain = GrainClient.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            logger.Info("{0} took {1}", what, clock.Elapsed);

            what = "EchoGrain.PingOtherSilo";
            clock.Restart();
            await grain.PingOtherSiloAsync().WithTimeout(timeout);
            logger.Info("{0} took {1}", what, clock.Elapsed);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Echo")]
        public async Task EchoGrain_PingSilo_OtherSilo_Membership()
        {
            Stopwatch clock = new Stopwatch();

            string what = "CreateGrain";
            clock.Start();
            grain = GrainClient.GrainFactory.GetGrain<IEchoTaskGrain>(Guid.NewGuid());
            logger.Info("{0} took {1}", what, clock.Elapsed);

            what = "EchoGrain.PingOtherSiloMembership";
            clock.Restart();
            await grain.PingClusterMemberAsync().WithTimeout(timeout);
            logger.Info("{0} took {1}", what, clock.Elapsed);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Echo")]
        public async Task EchoTaskGrain_Await()
        {
            IBlockingEchoTaskGrain g = GrainClient.GrainFactory.GetGrain<IBlockingEchoTaskGrain>(GetRandomGrainId());

            string received = await g.Echo(expectedEcho);
            Assert.AreEqual(expectedEcho, received, "Echo");

            received = await g.CallMethodAV_Await(expectedEcho);
            Assert.AreEqual(expectedEcho, received, "CallMethodAV_Await");

            received = await g.CallMethodTask_Await(expectedEcho);
            Assert.AreEqual(expectedEcho, received, "CallMethodTask_Await");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Echo")]
        public async Task EchoTaskGrain_Await_Reentrant()
        {
            IReentrantBlockingEchoTaskGrain g = GrainClient.GrainFactory.GetGrain<IReentrantBlockingEchoTaskGrain>(GetRandomGrainId());

            string received = await g.Echo(expectedEcho);
            Assert.AreEqual(expectedEcho, received, "Echo");

            received = await g.CallMethodAV_Await(expectedEcho);
            Assert.AreEqual(expectedEcho, received, "CallMethodAV_Await");

            received = await g.CallMethodTask_Await(expectedEcho);
            Assert.AreEqual(expectedEcho, received, "CallMethodTask_Await");
        }

        // ---------- Utility methods ----------

        private bool TimeIsLonger(TimeSpan time, TimeSpan limit)
        {
            logger.Info("Compare TimeIsLonger: Actual={0} Limit={1} Epsilon={2}", time, limit, Epsilon);
            return time >= (limit - Epsilon);
        }

        private bool TimeIsShorter(TimeSpan time, TimeSpan limit)
        {
            logger.Info("Compare TimeIsShorter: Actual={0} Limit={1} Epsilon={2}", time, limit, Epsilon);
            return time <= (limit + Epsilon);
        }
    }
}
