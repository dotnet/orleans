using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Providers;
using Orleans.Providers.Streams.SimpleMessageStream;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;

using Tester;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Tester;
using Xunit;
using Xunit.Abstractions;

#pragma warning disable 618

namespace UnitTests
{
    public class ReentrancyTests : OrleansTestingBase, IClassFixture<ReentrancyTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions();
                options.ClusterConfiguration.Globals.RegisterStreamProvider<SimpleMessageStreamProvider>("sms");
                options.ClusterConfiguration.AddMemoryStorageProvider("Default");
                options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
                options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
                return new TestCluster(options);
            }
        }

        private readonly ITestOutputHelper output;
        private readonly TestCluster hostedCluster;

        public ReentrancyTests(ITestOutputHelper output, Fixture fixture)
        {
            this.output = output;
            hostedCluster = fixture.HostedCluster;
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void ReentrantGrain()
        {
            var reentrant = GrainClient.GrainFactory.GetGrain<IReentrantGrain>(GetRandomGrainId());
            reentrant.SetSelf(reentrant).Wait();
            try
            {
                Assert.True(reentrant.Two().Wait(2000), "Grain should reenter");
            }
            catch (Exception ex)
            {
                Assert.True(false, string.Format("Unexpected exception {0}: {1}", ex.Message, ex.StackTrace));
            }
            logger.Info("Reentrancy ReentrantGrain Test finished OK.");
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void NonReentrantGrain()
        {
            INonReentrantGrain nonreentrant = GrainClient.GrainFactory.GetGrain<INonReentrantGrain>(GetRandomGrainId());
            nonreentrant.SetSelf(nonreentrant).Wait();
            bool timeout = false;
            bool deadlock = false;
            try
            {
                timeout = !nonreentrant.Two().Wait(2000);
            }
            catch (Exception exc)
            {
                Exception baseExc = exc.GetBaseException();
                if (baseExc.GetType().Equals(typeof(DeadlockException)))
                {
                    deadlock = true;
                }
                else
                {
                    Assert.True(false, string.Format("Unexpected exception {0}: {1}", exc.Message, exc.StackTrace));
                }
            }
            if (this.hostedCluster.Primary.Silo.GlobalConfig.PerformDeadlockDetection)
            {
                Assert.True(deadlock, "Non-reentrant grain should deadlock");
            }
            else
            {
                Assert.True(timeout, "Non-reentrant grain should timeout");
            }
            logger.Info("Reentrancy NonReentrantGrain Test finished OK.");
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void NonReentrantGrain_WithMayInterleavePredicate_WhenPredicateReturnsFalse()
        {
            var grain = GrainClient.GrainFactory.GetGrain<IMayInterleavePredicateGrain>(GetRandomGrainId());
            grain.SetSelf(grain).Wait();
            bool timeout = false;
            bool deadlock = false;
            try
            {
                timeout = !grain.Two().Wait(2000);
            }
            catch (Exception exc)
            {
                Exception baseExc = exc.GetBaseException();
                if (baseExc.GetType().Equals(typeof(DeadlockException)))
                {
                    deadlock = true;
                }
                else
                {
                    Assert.True(false, string.Format("Unexpected exception {0}: {1}", exc.Message, exc.StackTrace));
                }
            }
            if (this.hostedCluster.ClusterConfiguration.Globals.PerformDeadlockDetection)
            {
                Assert.True(deadlock, "Non-reentrant grain should deadlock when MayInterleave predicate returns false");
            }
            else
            {
                Assert.True(timeout, "Non-reentrant grain should timeout when MayInterleave predicate returns false");
            }
            logger.Info("Reentrancy NonReentrantGrain_WithMayInterleavePredicate_WhenPredicateReturnsFalse Test finished OK.");
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void NonReentrantGrain_WithMayInterleavePredicate_WhenPredicateReturnsTrue()
        {
            var grain = GrainClient.GrainFactory.GetGrain<IMayInterleavePredicateGrain>(GetRandomGrainId());
            grain.SetSelf(grain).Wait();
            try
            {
                Assert.True(grain.TwoReentrant().Wait(2000), "Grain should reenter when MayInterleave predicate returns true");
            }
            catch (Exception ex)
            {
                Assert.True(false, string.Format("Unexpected exception {0}: {1}", ex.Message, ex.StackTrace));
            }
            logger.Info("Reentrancy NonReentrantGrain_WithMayInterleavePredicate_WhenPredicateReturnsTrue Test finished OK.");
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void NonReentrantGrain_WithMessageInterleavesPredicate_StreamItemDelivery_WhenPredicateReturnsFalse()
        {
            var grain = GrainClient.GrainFactory.GetGrain<IMayInterleavePredicateGrain>(GetRandomGrainId());
            grain.SubscribeToStream().Wait();
            bool timeout = false;
            bool deadlock = false;
            try
            {
                timeout = !grain.PushToStream("foo").Wait(2000);
            }
            catch (Exception exc)
            {
                Exception baseExc = exc.GetBaseException();
                if (baseExc.GetType().Equals(typeof(DeadlockException)))
                {
                    deadlock = true;
                }
                else
                {
                    Assert.True(false, string.Format("Unexpected exception {0}: {1}", exc.Message, exc.StackTrace));
                }
            }
            if (this.hostedCluster.ClusterConfiguration.Globals.PerformDeadlockDetection)
            {
                Assert.True(deadlock, "Non-reentrant grain should deadlock on stream item delivery to itself when CanInterleave predicate returns false");
            }
            else
            {
                Assert.True(timeout, "Non-reentrant grain should timeout on stream item delivery to itself when CanInterleave predicate returns false");
            }
            logger.Info("Reentrancy NonReentrantGrain_WithMessageInterleavesPredicate_StreamItemDelivery_WhenPredicateReturnsFalse Test finished OK.");
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void NonReentrantGrain_WithMayInterleavePredicate_StreamItemDelivery_WhenPredicateReturnsTrue()
        {
            var grain = GrainClient.GrainFactory.GetGrain<IMayInterleavePredicateGrain>(GetRandomGrainId());
            grain.SubscribeToStream().Wait();
            try
            {
                grain.PushToStream("reentrant").Wait(2000);
            }
            catch (Exception ex)
            {
                Assert.True(false, string.Format("Unexpected exception {0}: {1}", ex.Message, ex.StackTrace));
            }
            logger.Info("Reentrancy NonReentrantGrain_WithMayInterleavePredicate_StreamItemDelivery_WhenPredicateReturnsTrue Test finished OK.");
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void NonReentrantGrain_WithMayInterleavePredicate_WhenPredicateThrows()
        {
            var grain = GrainClient.GrainFactory.GetGrain<IMayInterleavePredicateGrain>(GetRandomGrainId());
            grain.SetSelf(grain).Wait();
            try
            {
                grain.Exceptional().Wait(2000);
            }
            catch (Exception ex)
            {
                Assert.IsType<OrleansException>(ex.GetBaseException());
                Assert.NotNull(ex.GetBaseException().InnerException);
                Assert.IsType<ApplicationException>(ex.GetBaseException().InnerException);
                Assert.True(ex.GetBaseException().InnerException?.Message == "boom", 
                    "Should fail with Orleans runtime exception having all of neccessary details");
            }
            logger.Info("Reentrancy NonReentrantGrain_WithMayInterleavePredicate_WhenPredicateThrows Test finished OK.");
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void UnorderedNonReentrantGrain()
        {
            IUnorderedNonReentrantGrain unonreentrant = GrainClient.GrainFactory.GetGrain<IUnorderedNonReentrantGrain>(GetRandomGrainId());
            unonreentrant.SetSelf(unonreentrant).Wait();
            bool timeout = false;
            bool deadlock = false;
            try
            {
                timeout = !unonreentrant.Two().Wait(2000);
            }
            catch (Exception exc)
            {
                Exception baseExc = exc.GetBaseException();
                if (baseExc.GetType().Equals(typeof(DeadlockException)))
                {
                    deadlock = true;
                }
                else
                {
                    Assert.True(false, $"Unexpected exception {exc.Message}: {exc.StackTrace}");
                }
            }
            if (this.hostedCluster.Primary.Silo.GlobalConfig.PerformDeadlockDetection)
            {
                Assert.True(deadlock, "Non-reentrant grain should deadlock");
            }
            else
            {
                Assert.True(timeout, "Non-reentrant grain should timeout");
            }

            logger.Info("Reentrancy UnorderedNonReentrantGrain Test finished OK.");
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public async Task IsReentrant()
        {
            IReentrantTestSupportGrain grain = GrainClient.GrainFactory.GetGrain<IReentrantTestSupportGrain>(0);

            var grainFullName = typeof(ReentrantGrain).FullName;
            Assert.True(await grain.IsReentrant(grainFullName));
            grainFullName = typeof(NonRentrantGrain).FullName;
            Assert.False(await grain.IsReentrant(grainFullName));
            grainFullName = typeof(UnorderedNonRentrantGrain).FullName;
            Assert.False(await grain.IsReentrant(grainFullName));
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void Reentrancy_Deadlock_1()
        {
            List<Task> done = new List<Task>();
            var grain1 = GrainClient.GrainFactory.GetGrain<IReentrantSelfManagedGrain>(1);
            grain1.SetDestination(2).Wait();
            done.Add(grain1.Ping(15));

            var grain2 = GrainClient.GrainFactory.GetGrain<IReentrantSelfManagedGrain>(2);
            grain2.SetDestination(1).Wait();
            done.Add(grain2.Ping(15));

            Task.WhenAll(done).Wait();
            logger.Info("ReentrancyTest_Deadlock_1 OK - no deadlock.");
        }

        // TODO: [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        [Fact(Skip = "Ignore"), TestCategory("Failures"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void Reentrancy_Deadlock_2()
        {
            List<Task> done = new List<Task>();
            var grain1 = GrainClient.GrainFactory.GetGrain<INonReentrantSelfManagedGrain>(1);
            grain1.SetDestination(2).Wait();

            var grain2 = GrainClient.GrainFactory.GetGrain<INonReentrantSelfManagedGrain>(2);
            grain2.SetDestination(1).Wait();

            logger.Info("ReentrancyTest_Deadlock_2 is about to call grain1.Ping()");
            done.Add(grain1.Ping(15));
            logger.Info("ReentrancyTest_Deadlock_2 is about to call grain2.Ping()");
            done.Add(grain2.Ping(15));

            Task.WhenAll(done).Wait();
            logger.Info("ReentrancyTest_Deadlock_2 OK - no deadlock.");
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public async Task FanOut_Task_Reentrant()
        {
            await Do_FanOut_Task_Join(0, false, false);
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public async Task FanOut_Task_NonReentrant()
        {
            await Do_FanOut_Task_Join(0, true, false);
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public async Task FanOut_Task_Reentrant_Chain()
        {
            await Do_FanOut_Task_Join(0, false, true);
        }

        // TODO: [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        [Fact(Skip ="Ignore"), TestCategory("Failures"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public async Task FanOut_Task_NonReentrant_Chain()
        {
            await Do_FanOut_Task_Join(0, true, true);
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public async Task FanOut_AC_Reentrant()
        {
            await Do_FanOut_AC_Join(0, false, false);
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public async Task FanOut_AC_NonReentrant()
        {
            await Do_FanOut_AC_Join(0, true, false);
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public async Task FanOut_AC_Reentrant_Chain()
        {
            await Do_FanOut_AC_Join(0, false, true);
        }

        [TestCategory("MultithreadingFailures")]
        // TODO: [TestCategory("Functional")]
        [Fact(Skip ="Ignore"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public async Task FanOut_AC_NonReentrant_Chain()
        {
            await Do_FanOut_AC_Join(0, true, true);
        }

        [Fact, TestCategory("Stress"), TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void FanOut_Task_Stress_Reentrant()
        {
            const int numLoops = 5;
            const int blockSize = 10;
            TimeSpan timeout = TimeSpan.FromSeconds(40);
            Do_FanOut_Stress(numLoops, blockSize, timeout, false, false);
        }

        [Fact, TestCategory("Stress"), TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void FanOut_Task_Stress_NonReentrant()
        {
            const int numLoops = 5;
            const int blockSize = 10;
            TimeSpan timeout = TimeSpan.FromSeconds(40);
            Do_FanOut_Stress(numLoops, blockSize, timeout, true, false);
        }

        [Fact, TestCategory("Stress"), TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void FanOut_AC_Stress_Reentrant()
        {
            const int numLoops = 5;
            const int blockSize = 10;
            TimeSpan timeout = TimeSpan.FromSeconds(40);
            Do_FanOut_Stress(numLoops, blockSize, timeout, false, true);
        }

        [Fact, TestCategory("Stress"), TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void FanOut_AC_Stress_NonReentrant()
        {
            const int numLoops = 5;
            const int blockSize = 10;
            TimeSpan timeout = TimeSpan.FromSeconds(40);
            Do_FanOut_Stress(numLoops, blockSize, timeout, true, true);
        }

        // ---------- Utility methods ----------

        private async Task Do_FanOut_Task_Join(int offset, bool doNonReentrant, bool doCallChain)
        {
            const int num = 10;
            int id = random.Next();
            if (doNonReentrant)
            {
                IFanOutGrain grain = GrainClient.GrainFactory.GetGrain<IFanOutGrain>(id);
                if (doCallChain)
                {
                    await grain.FanOutNonReentrant_Chain(offset*num, num);
                }
                else
                {
                    await grain.FanOutNonReentrant(offset * num, num);
                }
            }
            else
            {
                IFanOutGrain grain = GrainClient.GrainFactory.GetGrain<IFanOutGrain>(id);
                if (doCallChain)
                {
                    await grain.FanOutReentrant_Chain(offset*num, num);
                }
                else
                {
                    await grain.FanOutReentrant(offset * num, num);
                }
            }
        }

        private async Task Do_FanOut_AC_Join(int offset, bool doNonReentrant, bool doCallChain)
        {
            const int num = 10;
            int id = random.Next();
            if (doNonReentrant)
            {
                IFanOutACGrain grain = GrainClient.GrainFactory.GetGrain<IFanOutACGrain>(id);
                if (doCallChain)
                {
                    await grain.FanOutACNonReentrant_Chain(offset * num, num);
                }
                else
                {
                    await grain.FanOutACNonReentrant(offset * num, num);
                }
            }
            else
            {
                IFanOutACGrain grain = GrainClient.GrainFactory.GetGrain<IFanOutACGrain>(id);
                if (doCallChain)
                {
                    await grain.FanOutACReentrant_Chain(offset * num, num);
                }
                else
                {
                    await grain.FanOutACReentrant(offset * num, num);
                }
            }
        }

        private readonly TimeSpan MaxStressExecutionTime = TimeSpan.FromMinutes(2);

        private void Do_FanOut_Stress(int numLoops, int blockSize, TimeSpan timeout,
            bool doNonReentrant, bool doAC)
        {
            Stopwatch totalTime = Stopwatch.StartNew();
            List<Task> promises = new List<Task>();
            for (int i = 0; i < numLoops; i++)
            {
                output.WriteLine("Start loop {0}", i);
                Stopwatch loopClock = Stopwatch.StartNew();
                for (int j = 0; j < blockSize; j++)
                {
                    int offset = j;
                    output.WriteLine("Start inner loop {0}", j);
                    Stopwatch innerClock = Stopwatch.StartNew();
                    Task promise = Task.Run(() =>
                    {
                        return doAC ? Do_FanOut_AC_Join(offset, doNonReentrant, false)
                                    : Do_FanOut_Task_Join(offset, doNonReentrant, false);
                    });
                    promises.Add(promise);
                    output.WriteLine("Inner loop {0} - Created Tasks. Elapsed={1}", j, innerClock.Elapsed);
                    bool ok = Task.WhenAll(promises).Wait(timeout);
                    if (!ok) throw new TimeoutException();
                    output.WriteLine("Inner loop {0} - Finished Join. Elapsed={1}", j, innerClock.Elapsed);
                    promises.Clear();
                }
                output.WriteLine("End loop {0} Elapsed={1}", i, loopClock.Elapsed);
            }
            TimeSpan elapsed = totalTime.Elapsed;
            Assert.True(elapsed < MaxStressExecutionTime, $"Stress test execution took too long: {elapsed}");
        }
    }
}

#pragma warning restore 618
