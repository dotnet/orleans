using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using Tester;
using UnitTests.GrainInterfaces;
using Xunit;
using UnitTests.Tester;

namespace UnitTests.General
{
    public class DeadlockDetectionTests : OrleansTestingBase, IClassFixture<DeadlockDetectionTests.Fixture>
    {
        private class Fixture : BaseClusterFixture
        {
            protected override TestingSiloHost CreateClusterHost()
            {
                return new TestingSiloHost(new TestingSiloOptions
                {
                    AdjustConfig = config =>
                    {
                        config.Globals.PerformDeadlockDetection = true;
                    }
                });
            }
        }

        private const int numIterations = 30;

        // 2 silos, loop across all cases (to force all grains to be local and remote):
        //      Non Reentrant A, B
        //      Reentrant C
        // 1) Deadlock A, A
        // 2) Deadlock A, B, A
        // 3) Deadlock C, A, C, A
        // 4) No Deadlock C, C
        // 5) No Deadlock C, A, C

        // 1) Deadlock A, A
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task DeadlockDetection_1()
        {
            long baseGrainId = random.Next();
            for (int i = 0; i < numIterations; i++)
            {
                long grainId = baseGrainId + i;
                IDeadlockNonReentrantGrain firstGrain = GrainClient.GrainFactory.GetGrain<IDeadlockNonReentrantGrain>(grainId);
                List<Tuple<long, bool>> callChain = new List<Tuple<long, bool>>();
                callChain.Add(new Tuple<long, bool>(grainId, true));
                callChain.Add(new Tuple<long, bool>(grainId, true));

                try
                {
                    await firstGrain.CallNext_1(callChain, 1);
                }
                catch (Exception exc)
                {
                    Exception baseExc = exc.GetBaseException();
                    logger.Info(baseExc.Message);
                    Assert.AreEqual(typeof(DeadlockException), baseExc.GetType());
                    DeadlockException deadlockExc = (DeadlockException)baseExc;
                    Assert.AreEqual(callChain.Count, deadlockExc.CallChain.Count());
                }
            }
        }

        // 2) Deadlock A, B, A
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task DeadlockDetection_2()
        {
            long baseGrainId = random.Next();
            long bBase = 100;
            for (int i = 0; i < numIterations; i++)
            {
                long grainId = baseGrainId + i;
                IDeadlockNonReentrantGrain firstGrain = GrainClient.GrainFactory.GetGrain<IDeadlockNonReentrantGrain>(grainId);
                List<Tuple<long, bool>> callChain = new List<Tuple<long, bool>>();
                callChain.Add(new Tuple<long, bool>(grainId, true));
                callChain.Add(new Tuple<long, bool>(bBase + grainId, true));
                callChain.Add(new Tuple<long, bool>(grainId, true));

                try
                {
                    await firstGrain.CallNext_1(callChain, 1);
                }
                catch (Exception exc)
                {
                    Exception baseExc = exc.GetBaseException();
                    logger.Info(baseExc.Message);
                    Assert.AreEqual(typeof(DeadlockException), baseExc.GetType());
                    DeadlockException deadlockExc = (DeadlockException)baseExc;
                    Assert.AreEqual(callChain.Count, deadlockExc.CallChain.Count());
                }
            }
        }

        // 3) Deadlock C, A, C, A
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task DeadlockDetection_3()
        {
            long baseGrainId = random.Next();
            long cBase = 200;
            for (int i = 0; i < numIterations; i++)
            {
                long grainId = baseGrainId + i;
                IDeadlockReentrantGrain firstGrain = GrainClient.GrainFactory.GetGrain<IDeadlockReentrantGrain>(grainId);
                List<Tuple<long, bool>> callChain = new List<Tuple<long, bool>>();
                callChain.Add(new Tuple<long, bool>(cBase + grainId, false));
                callChain.Add(new Tuple<long, bool>(grainId, true));
                callChain.Add(new Tuple<long, bool>(cBase + grainId, false));
                callChain.Add(new Tuple<long, bool>(grainId, true));

                try
                {
                    await firstGrain.CallNext_1(callChain, 1);
                }
                catch (Exception exc)
                {
                    Exception baseExc = exc.GetBaseException();
                    logger.Info(baseExc.Message);
                    Assert.AreEqual(typeof(DeadlockException), baseExc.GetType());
                    DeadlockException deadlockExc = (DeadlockException)baseExc;
                    Assert.AreEqual(callChain.Count, deadlockExc.CallChain.Count());
                }
            }
        }

        // 4) No Deadlock C, C
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task DeadlockDetection_4()
        {
            long baseGrainId = random.Next();
            long cBase = 200;
            for (int i = 0; i < numIterations; i++)
            {
                long grainId = baseGrainId + i;
                IDeadlockReentrantGrain firstGrain = GrainClient.GrainFactory.GetGrain<IDeadlockReentrantGrain>(grainId);
                List<Tuple<long, bool>> callChain = new List<Tuple<long, bool>>();
                callChain.Add(new Tuple<long, bool>(cBase + grainId, false));
                callChain.Add(new Tuple<long, bool>(cBase + grainId, false));

                await firstGrain.CallNext_1(callChain, 1);
            }
        }

        // 5) No Deadlock C, A, C
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task DeadlockDetection_5()
        {
            long baseGrainId = random.Next();
            long cBase = 200;
            for (int i = 0; i < numIterations; i++)
            {
                long grainId = baseGrainId + i;
                IDeadlockReentrantGrain firstGrain = GrainClient.GrainFactory.GetGrain<IDeadlockReentrantGrain>(grainId);
                List<Tuple<long, bool>> callChain = new List<Tuple<long, bool>>();
                callChain.Add(new Tuple<long, bool>(cBase + grainId, false));
                callChain.Add(new Tuple<long, bool>(grainId, true));
                callChain.Add(new Tuple<long, bool>(cBase + grainId, false));

                await firstGrain.CallNext_1(callChain, 1);
            }
        }
    }
}

