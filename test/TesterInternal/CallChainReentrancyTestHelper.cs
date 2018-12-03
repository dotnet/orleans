using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TestExtensions;
using UnitTests.GrainInterfaces;

namespace UnitTests.General
{
    public class CallChainReentrancyTestHelper
    {
        public Random Random { get; set; }
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
        public async Task DeadlockDetection_1()
        {
            long baseGrainId = this.Random.Next();
            for (int i = 0; i < this.NumIterations; i++)
            {
                long grainId = baseGrainId + i;
                IDeadlockNonReentrantGrain firstGrain = this.Fixture.GrainFactory.GetGrain<IDeadlockNonReentrantGrain>(grainId);
                List<Tuple<long, bool>> callChain = new List<Tuple<long, bool>>();
                callChain.Add(new Tuple<long, bool>(grainId, true));
                callChain.Add(new Tuple<long, bool>(grainId, true));
                await firstGrain.CallNext_1(callChain, 1);
            }
        }

        // 2) Allowed reentrancy on non-reentrant grains A, B, A
        public async Task DeadlockDetection_2()
        {
            long baseGrainId = this.Random.Next();
            long bBase = 100;
            for (int i = 0; i < this.NumIterations; i++)
            {
                long grainId = baseGrainId + i;
                IDeadlockNonReentrantGrain firstGrain = this.Fixture.GrainFactory.GetGrain<IDeadlockNonReentrantGrain>(grainId);
                List<Tuple<long, bool>> callChain = new List<Tuple<long, bool>>();
                callChain.Add(new Tuple<long, bool>(grainId, true));
                callChain.Add(new Tuple<long, bool>(bBase + grainId, true));
                callChain.Add(new Tuple<long, bool>(grainId, true));
                await firstGrain.CallNext_1(callChain, 1);
            }
        }

        // 3) Allowed reentrancy X, A, X, A
        public async Task DeadlockDetection_3()
        {
            long baseGrainId = this.Random.Next();
            long xBase = 1000;
            for (int i = 0; i < this.NumIterations; i++)
            {
                long grainId = baseGrainId + i;
                IDeadlockReentrantGrain firstGrain = this.Fixture.GrainFactory.GetGrain<IDeadlockReentrantGrain>(grainId);
                List<Tuple<long, bool>> callChain = new List<Tuple<long, bool>>();
                callChain.Add(new Tuple<long, bool>(xBase + grainId, false));
                callChain.Add(new Tuple<long, bool>(grainId, true));
                callChain.Add(new Tuple<long, bool>(xBase + grainId, false));
                callChain.Add(new Tuple<long, bool>(grainId, true));
                await firstGrain.CallNext_1(callChain, 1);
            }
        }

        // 4) No Deadlock X, X
        public async Task DeadlockDetection_4()
        {
            long baseGrainId = this.Random.Next();
            long xBase = 1000;
            for (int i = 0; i < this.NumIterations; i++)
            {
                long grainId = baseGrainId + i;
                IDeadlockReentrantGrain firstGrain = this.Fixture.GrainFactory.GetGrain<IDeadlockReentrantGrain>(grainId);
                List<Tuple<long, bool>> callChain = new List<Tuple<long, bool>>();
                callChain.Add(new Tuple<long, bool>(xBase + grainId, false));
                callChain.Add(new Tuple<long, bool>(xBase + grainId, false));

                await firstGrain.CallNext_1(callChain, 1);
            }
        }

        // 5) No Deadlock X, A, X
        public async Task DeadlockDetection_5()
        {
            long baseGrainId = this.Random.Next();
            long xBase = 1000;
            for (int i = 0; i < this.NumIterations; i++)
            {
                long grainId = baseGrainId + i;
                IDeadlockReentrantGrain firstGrain = this.Fixture.GrainFactory.GetGrain<IDeadlockReentrantGrain>(grainId);
                List<Tuple<long, bool>> callChain = new List<Tuple<long, bool>>();
                callChain.Add(new Tuple<long, bool>(xBase + grainId, false));
                callChain.Add(new Tuple<long, bool>(grainId, true));
                callChain.Add(new Tuple<long, bool>(xBase + grainId, false));

                await firstGrain.CallNext_1(callChain, 1);
            }
        }

        // 6) Allowed reentrancy on non-reentrant grains only when using full chain reentrancy A, B, C, A
        public async Task DeadlockDetection_6()
        {
            long baseGrainId = this.Random.Next();
            long bBase = 100;
            long cBase = 200;
            for (int i = 0; i < this.NumIterations; i++)
            {
                long grainId = baseGrainId + i;
                IDeadlockNonReentrantGrain firstGrain = this.Fixture.GrainFactory.GetGrain<IDeadlockNonReentrantGrain>(grainId);
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