using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Internal;
using TestExtensions;
using UnitTests.GrainInterfaces;

namespace UnitTests.General
{
    public class CallChainReentrancyTestHelper
    {
        public BaseTestClusterFixture Fixture { get; set; }
        public int NumIterations { get; set; }

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
        public async Task CallChainReentrancy_1()
        {
            long baseGrainId = Random.Shared.Next();
            for (var i = 0; i < NumIterations; i++)
            {
                var grainId = baseGrainId + i;
                var firstGrain = Fixture.GrainFactory.GetGrain<IDeadlockNonReentrantGrain>(grainId);
                var callChain = new List<(long GrainId, bool Blocking)>
                {
                    (grainId, true),
                    (grainId, true)
                };
                await firstGrain.CallNext_1(callChain, 1);
            }
        }

        // 2) Allowed reentrancy on non-reentrant grains A, B, A
        public async Task CallChainReentrancy_2()
        {
            long baseGrainId = Random.Shared.Next();
            for (var i = 0; i < NumIterations; i++)
            {
                var grainId = baseGrainId + i;
                var firstGrain = Fixture.GrainFactory.GetGrain<IDeadlockNonReentrantGrain>(grainId);
                var callChain = new List<(long GrainId, bool Blocking)>
                {
                    (grainId, true),
                    (grainId + 100, true),
                    (grainId, true)
                };
                await firstGrain.CallNext_1(callChain, 1);
            }
        }

        // 3) Allowed reentrancy X, A, X, A
        public async Task CallChainReentrancy_3()
        {
            long baseGrainId = Random.Shared.Next();
            for (var i = 0; i < NumIterations; i++)
            {
                var grainId = baseGrainId + i;
                var firstGrain = Fixture.GrainFactory.GetGrain<IDeadlockReentrantGrain>(grainId);
                var callChain = new List<(long GrainId, bool Blocking)>
                {
                    (grainId + 1000, false),
                    (grainId, true),
                    (grainId + 1000, false),
                    (grainId, true)
                };
                await firstGrain.CallNext_1(callChain, 1);
            }
        }

        // 4) No Deadlock X, X
        public async Task CallChainReentrancy_4()
        {
            long baseGrainId = Random.Shared.Next();
            for (var i = 0; i < NumIterations; i++)
            {
                var grainId = baseGrainId + i;
                var firstGrain = Fixture.GrainFactory.GetGrain<IDeadlockReentrantGrain>(grainId);
                var callChain = new List<(long GrainId, bool Blocking)>
                {
                    (grainId + 1000, false),
                    (grainId + 1000, false)
                };

                await firstGrain.CallNext_1(callChain, 1);
            }
        }

        // 5) No Deadlock X, A, X
        public async Task CallChainReentrancy_5()
        {
            long baseGrainId = Random.Shared.Next();
            for (var i = 0; i < NumIterations; i++)
            {
                var grainId = baseGrainId + i;
                var firstGrain = Fixture.GrainFactory.GetGrain<IDeadlockReentrantGrain>(grainId);
                var callChain = new List<(long GrainId, bool Blocking)>
                {
                    (grainId + 1000, false),
                    (grainId, true),
                    (grainId + 1000, false)
                };

                await firstGrain.CallNext_1(callChain, 1);
            }
        }

        // 6) Allowed reentrancy on non-reentrant grains only when using full chain reentrancy A, B, C, A
        public async Task CallChainReentrancy_6()
        {
            long baseGrainId = Random.Shared.Next();
            for (var i = 0; i < NumIterations; i++)
            {
                var grainId = baseGrainId + i;
                var firstGrain = Fixture.GrainFactory.GetGrain<IDeadlockNonReentrantGrain>(grainId);
                var callChain = new List<(long GrainId, bool Blocking)>
                {
                    (grainId, true),
                    (grainId + 100, true),
                    (grainId + 200, true),
                    (grainId, true)
                };
                await firstGrain.CallNext_1(callChain, 1);
            }
        }
    }
} 
