using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Xunit;
using TestExtensions;
using Orleans.Hosting;
using Orleans.Configuration;

namespace UnitTests.General
{
    public class DeadlockDetectionWithAllowCallChainSingleCallReentrancyTests : OrleansTestingBase, IClassFixture<DeadlockDetectionWithAllowCallChainSingleCallReentrancyTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }

            private class SiloConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder.Configure<SchedulingOptions>(options =>
                    {
                        options.PerformDeadlockDetection = true;
                        options.CallChainReentrancy = SchedulingOptions.CallChainReentrancyStrategy.SingleCall;
                    });
                }
            }

        }

        private const int numIterations = 30;

        public DeadlockDetectionWithAllowCallChainSingleCallReentrancyTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        // 2 silos, loop across all cases (to force all grains to be local and remote):
        //      Non Reentrant A, B, C
        //      Reentrant X
        // 1) No Deadlock A, A
        // 2) No Deadlock A, B, A
        // 3) No Deadlock X, A, X, A
        // 4) No Deadlock X, X
        // 5) No Deadlock X, A, X
        // 6) No Deadlock A, B, C, A

        // 1) Allowed reentrancy A, A
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task DeadlockDetection_1()
        {
            long baseGrainId = random.Next();
            for (int i = 0; i < numIterations; i++)
            {
                long grainId = baseGrainId + i;
                IDeadlockNonReentrantGrain firstGrain = this.fixture.GrainFactory.GetGrain<IDeadlockNonReentrantGrain>(grainId);
                List<Tuple<long, bool>> callChain = new List<Tuple<long, bool>>();
                callChain.Add(new Tuple<long, bool>(grainId, true));
                callChain.Add(new Tuple<long, bool>(grainId, true));
                await firstGrain.CallNext_1(callChain, 1);
            }
        }

        // 2) Allowed reentrancy on non-reentrant grains A, B, A
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task DeadlockDetection_2()
        {
            long baseGrainId = random.Next();
            long bBase = 100;
            for (int i = 0; i < numIterations; i++)
            {
                long grainId = baseGrainId + i;
                IDeadlockNonReentrantGrain firstGrain = this.fixture.GrainFactory.GetGrain<IDeadlockNonReentrantGrain>(grainId);
                List<Tuple<long, bool>> callChain = new List<Tuple<long, bool>>();
                callChain.Add(new Tuple<long, bool>(grainId, true));
                callChain.Add(new Tuple<long, bool>(bBase + grainId, true));
                callChain.Add(new Tuple<long, bool>(grainId, true));
                await firstGrain.CallNext_1(callChain, 1);
            }
        }

        // 3) Allowed reentrancy X, A, X, A
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task DeadlockDetection_3()
        {
            long baseGrainId = random.Next();
            long xBase = 1000;
            for (int i = 0; i < numIterations; i++)
            {
                long grainId = baseGrainId + i;
                IDeadlockReentrantGrain firstGrain = this.fixture.GrainFactory.GetGrain<IDeadlockReentrantGrain>(grainId);
                List<Tuple<long, bool>> callChain = new List<Tuple<long, bool>>();
                callChain.Add(new Tuple<long, bool>(xBase + grainId, false));
                callChain.Add(new Tuple<long, bool>(grainId, true));
                callChain.Add(new Tuple<long, bool>(xBase + grainId, false));
                callChain.Add(new Tuple<long, bool>(grainId, true));
                await firstGrain.CallNext_1(callChain, 1);
            }
        }

        // 4) No Deadlock X, X
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task DeadlockDetection_4()
        {
            long baseGrainId = random.Next();
            long xBase = 1000;
            for (int i = 0; i < numIterations; i++)
            {
                long grainId = baseGrainId + i;
                IDeadlockReentrantGrain firstGrain = this.fixture.GrainFactory.GetGrain<IDeadlockReentrantGrain>(grainId);
                List<Tuple<long, bool>> callChain = new List<Tuple<long, bool>>();
                callChain.Add(new Tuple<long, bool>(xBase + grainId, false));
                callChain.Add(new Tuple<long, bool>(xBase + grainId, false));

                await firstGrain.CallNext_1(callChain, 1);
            }
        }

        // 5) No Deadlock X, A, X
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task DeadlockDetection_5()
        {
            long baseGrainId = random.Next();
            long xBase = 1000;
            for (int i = 0; i < numIterations; i++)
            {
                long grainId = baseGrainId + i;
                IDeadlockReentrantGrain firstGrain = this.fixture.GrainFactory.GetGrain<IDeadlockReentrantGrain>(grainId);
                List<Tuple<long, bool>> callChain = new List<Tuple<long, bool>>();
                callChain.Add(new Tuple<long, bool>(xBase + grainId, false));
                callChain.Add(new Tuple<long, bool>(grainId, true));
                callChain.Add(new Tuple<long, bool>(xBase + grainId, false));

                await firstGrain.CallNext_1(callChain, 1);
            }
        }

        // 6) Allowed reentrancy on non-reentrant grains A, B, C, A
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task DeadlockDetection_6()
        {
            long baseGrainId = random.Next();
            long bBase = 100;
            long cBase = 200;
            for (int i = 0; i < numIterations; i++)
            {
                long grainId = baseGrainId + i;
                IDeadlockNonReentrantGrain firstGrain = this.fixture.GrainFactory.GetGrain<IDeadlockNonReentrantGrain>(grainId);
                List<Tuple<long, bool>> callChain = new List<Tuple<long, bool>>();
                callChain.Add(new Tuple<long, bool>(grainId, true));
                callChain.Add(new Tuple<long, bool>(bBase + grainId, true));
                callChain.Add(new Tuple<long, bool>(cBase + grainId, true));
                callChain.Add(new Tuple<long, bool>(grainId, true));
                await firstGrain.CallNext_1(callChain, 1);
            }
        }
    }
}
