using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Tester;

#pragma warning disable 618

namespace UnitTests
{
    [TestClass]
    public class ReentrancyTests : UnitTestSiloHost
    {
        private const bool DebugMode = 
#if DEBUG
            true; // TEST HACK
#else
            false;
#endif
        private static readonly TestingSiloOptions testOptions = new TestingSiloOptions
        {
            StartPrimary = true, 
            StartSecondary = !DebugMode
        };

        public ReentrancyTests() : base(testOptions)
        { }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            try
            {
                //ResetDefaultRuntimes();
                StopAllSilos();
            }
            catch (Exception ex)
            {
                Assert.Fail("ClassCleanup unexpected exception {0}: {1}", ex.Message, ex.StackTrace);
            }
        }

        [TestCleanup]
        public void TestCleanup()
        {
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void ReentrantGrain()
        {
            var reentrant = GrainClient.GrainFactory.GetGrain<IReentrantGrain>(GetRandomGrainId());
            reentrant.SetSelf(reentrant).Wait();
            try
            {
                Assert.IsTrue(reentrant.Two().Wait(2000), "Grain should reenter");
            }
            catch (Exception ex)
            {
                Assert.Fail("Unexpected exception {0}: {1}", ex.Message, ex.StackTrace);
            }
            logger.Info("Reentrancy ReentrantGrain Test finished OK.");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
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
                    Assert.Fail("Unexpected exception {0}: {1}", exc.Message, exc.StackTrace);
                }
            }
            if (Primary.Silo.GlobalConfig.PerformDeadlockDetection)
            {
                Assert.IsTrue(deadlock, "Non-reentrant grain should deadlock");
            }
            else
            {
                Assert.IsTrue(timeout, "Non-reentrant grain should timeout");
            }
            logger.Info("Reentrancy NonReentrantGrain Test finished OK.");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
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
                    Assert.Fail("Unexpected exception {0}: {1}", exc.Message, exc.StackTrace);
                }
            }
            if (Primary.Silo.GlobalConfig.PerformDeadlockDetection)
            {
                Assert.IsTrue(deadlock, "Non-reentrant grain should deadlock");
            }
            else
            {
                Assert.IsTrue(timeout, "Non-reentrant grain should timeout");
            }

            logger.Info("Reentrancy UnorderedNonReentrantGrain Test finished OK.");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public async Task IsReentrant()
        {
            IReentrantTestSupportGrain grain = GrainClient.GrainFactory.GetGrain<IReentrantTestSupportGrain>(0);

            var grainFullName = typeof(ReentrantGrain).FullName;
            Assert.IsTrue(await grain.IsReentrant(grainFullName));
            grainFullName = typeof(NonRentrantGrain).FullName;
            Assert.IsFalse(await grain.IsReentrant(grainFullName));
            grainFullName = typeof(UnorderedNonRentrantGrain).FullName;
            Assert.IsFalse(await grain.IsReentrant(grainFullName));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
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

        // TODO: [TestMethod, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        [TestMethod, TestCategory("Failures"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        [Ignore]
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

        [TestMethod, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public async Task FanOut_Task_Reentrant()
        {
            await Do_FanOut_Task_Join(0, false, false);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public async Task FanOut_Task_NonReentrant()
        {
            await Do_FanOut_Task_Join(0, true, false);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public async Task FanOut_Task_Reentrant_Chain()
        {
            await Do_FanOut_Task_Join(0, false, true);
        }

        // TODO: [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        [TestMethod, TestCategory("Failures"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        [Ignore]
        public async Task FanOut_Task_NonReentrant_Chain()
        {
            await Do_FanOut_Task_Join(0, true, true);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public async Task FanOut_AC_Reentrant()
        {
            await Do_FanOut_AC_Join(0, false, false);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public async Task FanOut_AC_NonReentrant()
        {
            await Do_FanOut_AC_Join(0, true, false);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public async Task FanOut_AC_Reentrant_Chain()
        {
            await Do_FanOut_AC_Join(0, false, true);
        }

        [TestCategory("MultithreadingFailures")]
        // TODO: [TestCategory("Functional")]
        [TestMethod, TestCategory("Tasks"), TestCategory("Reentrancy")]
        [Ignore]
        public async Task FanOut_AC_NonReentrant_Chain()
        {
            await Do_FanOut_AC_Join(0, true, true);
        }

        [TestMethod, TestCategory("Stress"), TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void FanOut_Task_Stress_Reentrant()
        {
            const int numLoops = 5;
            const int blockSize = 10;
            TimeSpan timeout = TimeSpan.FromSeconds(40);
            Do_FanOut_Stress(numLoops, blockSize, timeout, false, false);
        }

        [TestMethod, TestCategory("Stress"), TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void FanOut_Task_Stress_NonReentrant()
        {
            const int numLoops = 5;
            const int blockSize = 10;
            TimeSpan timeout = TimeSpan.FromSeconds(40);
            Do_FanOut_Stress(numLoops, blockSize, timeout, true, false);
        }

        [TestMethod, TestCategory("Stress"), TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void FanOut_AC_Stress_Reentrant()
        {
            const int numLoops = 5;
            const int blockSize = 10;
            TimeSpan timeout = TimeSpan.FromSeconds(40);
            Do_FanOut_Stress(numLoops, blockSize, timeout, false, true);
        }

        [TestMethod, TestCategory("Stress"), TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
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
                Console.WriteLine("Start loop {0}", i);
                Stopwatch loopClock = Stopwatch.StartNew();
                for (int j = 0; j < blockSize; j++)
                {
                    int offset = j;
                    Console.WriteLine("Start inner loop {0}", j);
                    Stopwatch innerClock = Stopwatch.StartNew();
                    Task promise = Task.Run(() =>
                    {
                        return doAC ? Do_FanOut_AC_Join(offset, doNonReentrant, false)
                                    : Do_FanOut_Task_Join(offset, doNonReentrant, false);
                    });
                    promises.Add(promise);
                    Console.WriteLine("Inner loop {0} - Created Tasks. Elapsed={1}", j, innerClock.Elapsed);
                    bool ok = Task.WhenAll(promises).Wait(timeout);
                    if (!ok) throw new TimeoutException();
                    Console.WriteLine("Inner loop {0} - Finished Join. Elapsed={1}", j, innerClock.Elapsed);
                    promises.Clear();
                }
                Console.WriteLine("End loop {0} Elapsed={1}", i, loopClock.Elapsed);
            }
            TimeSpan elapsed = totalTime.Elapsed;
            Assert.IsTrue(elapsed < MaxStressExecutionTime, "Stress test execution took too long: {0}", elapsed);
        }
    }
}

#pragma warning restore 618
