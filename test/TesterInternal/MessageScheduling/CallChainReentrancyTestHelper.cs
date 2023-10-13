using System.Threading.Channels;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

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

        /// <summary>
        /// Suppresses call chain reentrancy within a reentrant call chain, to ensure that the subsequent call chain is not reentrant.
        /// </summary>
        public async Task CallChainReentrancy_WithSuppression()
        {
            var aId = $"A-{Random.Shared.Next()}";
            var bId = $"B-{Random.Shared.Next()}";
            var a = Fixture.GrainFactory.GetGrain<ICallChainReentrancyGrain>(aId);

            var chain = new List<(string, ReentrancyCallType)>
            {
                // 1. a->a: Start a new call chain, allowing reentrancy back into 'a'
                (aId, ReentrancyCallType.AllowCallChainReentrancy),

                // 2. a->b: Wont allow reentrancy into 'b', since 'b' has not excxplicitly requested it.
                (bId, ReentrancyCallType.Regular),

                // 3. b->a: Ensure that reentrancy into 'a' is allowed.
                (aId, ReentrancyCallType.Regular),

                // 4. a->b: Suppress call chain reentrancy from 'a' to 'b'. The call to 'b' should block (until we explicitly unblock it later), since 'b' is waiting already.
                (bId, ReentrancyCallType.SuppressCallChainReentrancy),
            };

            var observer = new CallChainObserver();
            var observerRef = Fixture.GrainFactory.CreateObjectReference<ICallChainObserver>(observer);

            var aTask = a.CallChain(observerRef, chain, 0);

            // Wait for 'a' to receive the call from 'b'
            await observer.WaitForOperationAsync(CallChainOperation.Enter, aId, 3);

            // Tell 'a' to stop waiting for 'b to return, allowing the final call (step 4) to enter 'b' and complete the call chain.
            await a.UnblockWaiters();

            await aTask;

            // Wait for 'b' to complete its final call
            await observer.WaitForOperationAsync(CallChainOperation.Exit, bId, 4);
        }

        public enum CallChainOperation
        {
            Enter,
            Exit,
        }

        public class CallChainObserver : ICallChainObserver
        {
            public Channel<(CallChainOperation Operation, string Grain, int CallIndex)> Operations { get; } = Channel.CreateUnbounded<(CallChainOperation Operation, string Grain, int CallIndex)>();

            public async Task OnEnter(string grain, int callIndex)
            {
                await Operations.Writer.WriteAsync((CallChainOperation.Enter, grain, callIndex));
            }

            public async Task OnExit(string grain, int callIndex)
            {
                await Operations.Writer.WriteAsync((CallChainOperation.Exit, grain, callIndex));
            }

            public async Task WaitForOperationAsync(CallChainOperation operationType, string grain, int callIndex)
            {
                List<(CallChainOperation Operation, string Grain, int CallIndex)> ops = new();
                var operations = Operations.Reader;
                while (await operations.WaitToReadAsync())
                {
                    Assert.True(operations.TryRead(out var operation));
                    ops.Add(operation);

                    if (operation.Operation == operationType && operation.Grain.Equals(grain) && operation.CallIndex == callIndex)
                    {
                        return;
                    }
                    if (operation.CallIndex > callIndex)
                    {
                        break;
                    }
                }

                Assert.Fail($"Expected to find operation ({operationType}, {grain}, {callIndex}) in {string.Join(", ", ops.Select(op => $"({op.Operation}, {op.Grain}, {op.CallIndex})"))}");
            }
        }
    }
} 
