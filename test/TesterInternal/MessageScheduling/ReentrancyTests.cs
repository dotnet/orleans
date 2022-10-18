using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests
{
    internal class ReentrancyTestsSiloBuilderConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
        {
            hostBuilder
                .AddMemoryGrainStorage("MemoryStore")
                .AddMemoryGrainStorage("PubSubStore")
                .AddMemoryGrainStorageAsDefault();
        }
    }

    public class ReentrancyTests : OrleansTestingBase, IClassFixture<ReentrancyTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<ReentrancyTestsSiloBuilderConfigurator>();
            }
        }

        private readonly ITestOutputHelper output;
        private readonly Fixture fixture;

        public ReentrancyTests(ITestOutputHelper output, Fixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
        }

        // See https://github.com/dotnet/orleans/pull/5086
        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public async Task CorrelationId_Bug()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IFirstGrain>(Guid.NewGuid());
            await grain.Start(Guid.NewGuid(), Guid.NewGuid());
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void ReentrantGrain()
        {
            var reentrant = this.fixture.GrainFactory.GetGrain<IReentrantGrain>(GetRandomGrainId());
            reentrant.SetSelf(reentrant).Wait();
            try
            {
                Assert.True(reentrant.Two().Wait(2000), "Grain should reenter");
            }
            catch (Exception ex)
            {
                Assert.True(false, string.Format("Unexpected exception {0}: {1}", ex.Message, ex.StackTrace));
            }
            this.fixture.Logger.LogInformation("Reentrancy ReentrantGrain Test finished OK.");
        }
        
        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void NonReentrantGrain_WithMayInterleavePredicate_WhenPredicateReturnsTrue()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IMayInterleavePredicateGrain>(GetRandomGrainId());
            grain.SetSelf(grain).Wait();
            try
            {
                Assert.True(grain.TwoReentrant().Wait(2000), "Grain should reenter when MayInterleave predicate returns true");
            }
            catch (Exception ex)
            {
                Assert.True(false, string.Format("Unexpected exception {0}: {1}", ex.Message, ex.StackTrace));
            }
            this.fixture.Logger.LogInformation("Reentrancy NonReentrantGrain_WithMayInterleavePredicate_WhenPredicateReturnsTrue Test finished OK.");
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public async Task NonReentrantGrain_WithMayInterleavePredicate_WhenPredicateThrows()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IMayInterleavePredicateGrain>(GetRandomGrainId());
            grain.SetSelf(grain).Wait();
            try
            {
                await grain.Exceptional().WithTimeout(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                Assert.IsType<ApplicationException>(ex);
                Assert.True(ex.Message == "boom",
                    "Should fail with Orleans runtime exception having all of necessary details");
            }
            this.fixture.Logger.LogInformation("Reentrancy NonReentrantGrain_WithMayInterleavePredicate_WhenPredicateThrows Test finished OK.");
        }
        
        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void Reentrancy_Deadlock_1()
        {
            List<Task> done = new List<Task>();
            var grain1 = this.fixture.GrainFactory.GetGrain<IReentrantSelfManagedGrain>(1);
            grain1.SetDestination(2).Wait();
            done.Add(grain1.Ping(15));

            var grain2 = this.fixture.GrainFactory.GetGrain<IReentrantSelfManagedGrain>(2);
            grain2.SetDestination(1).Wait();
            done.Add(grain2.Ping(15));

            Task.WhenAll(done).Wait();
            this.fixture.Logger.LogInformation("ReentrancyTest_Deadlock_1 OK - no deadlock.");
        }

        // TODO: [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        [Fact(Skip = "Ignore"), TestCategory("Failures"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void Reentrancy_Deadlock_2()
        {
            List<Task> done = new List<Task>();
            var grain1 = this.fixture.GrainFactory.GetGrain<INonReentrantSelfManagedGrain>(1);
            grain1.SetDestination(2).Wait();

            var grain2 = this.fixture.GrainFactory.GetGrain<INonReentrantSelfManagedGrain>(2);
            grain2.SetDestination(1).Wait();

            this.fixture.Logger.LogInformation("ReentrancyTest_Deadlock_2 is about to call grain1.Ping()");
            done.Add(grain1.Ping(15));
            this.fixture.Logger.LogInformation("ReentrancyTest_Deadlock_2 is about to call grain2.Ping()");
            done.Add(grain2.Ping(15));

            Task.WhenAll(done).Wait();
            this.fixture.Logger.LogInformation("ReentrancyTest_Deadlock_2 OK - no deadlock.");
        }

        [Fact, TestCategory("Failures"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        private async Task NonReentrantFanOut()
        {
            var grain = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<int>>(Guid.NewGuid());
            var target = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<int>>(Guid.NewGuid());
            await grain.CallOtherLongRunningTask(target, 2, TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<TimeoutException>(
                () => target.FanOutOtherLongRunningTask(grain, 2, TimeSpan.FromSeconds(10), 5));
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

        // TODO: [Fact, TestCategory("BVT"), TestCategory("Tasks"), TestCategory("Reentrancy")]
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
            int id = Random.Shared.Next();
            if (doNonReentrant)
            {
                IFanOutGrain grain = this.fixture.GrainFactory.GetGrain<IFanOutGrain>(id);
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
                IFanOutGrain grain = this.fixture.GrainFactory.GetGrain<IFanOutGrain>(id);
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
            int id = Random.Shared.Next();
            if (doNonReentrant)
            {
                IFanOutACGrain grain = this.fixture.GrainFactory.GetGrain<IFanOutACGrain>(id);
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
                IFanOutACGrain grain = this.fixture.GrainFactory.GetGrain<IFanOutACGrain>(id);
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