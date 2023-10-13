#nullable enable

using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    internal class DeadlockGrain
    {
        internal static Task CallNext(IGrainFactory grainFactory, List<(long GrainId, bool Blocking)> callChain, int currCallIndex)
        {
            if (currCallIndex >= callChain.Count) return Task.CompletedTask;
            (long GrainId, bool Blocking) next = callChain[currCallIndex];
            bool call_1 = (currCallIndex % 2) == 1; // odd (1) call 1, even (zero) - call 2.
            if (next.Blocking)
            {
                IDeadlockNonReentrantGrain nextGrain = grainFactory.GetGrain<IDeadlockNonReentrantGrain>(next.GrainId);
                if (call_1)
                    return nextGrain.CallNext_1(callChain, currCallIndex + 1);
                else
                    return nextGrain.CallNext_2(callChain, currCallIndex + 1);
            }
            else
            {
                IDeadlockReentrantGrain nextGrain = grainFactory.GetGrain<IDeadlockReentrantGrain>(next.GrainId);
                if (call_1)
                    return nextGrain.CallNext_1(callChain, currCallIndex + 1);
                else
                    return nextGrain.CallNext_2(callChain, currCallIndex + 1);
            }
        }
    }

    public class DeadlockNonReentrantGrain : Grain, IDeadlockNonReentrantGrain
    {
        private readonly ILogger logger;
        public DeadlockNonReentrantGrain(ILoggerFactory loggerFactory) => this.logger = loggerFactory.CreateLogger(this.Id);
        private string Id { get { return string.Format("DeadlockNonReentrantGrain {0}", base.IdentityString); } }

        public async Task CallNext_1(List<(long GrainId, bool Blocking)> callChain, int currCallIndex)
        {
            using var _ = RequestContext.AllowCallChainReentrancy();
            this.logger.LogInformation("Inside grain {Id} CallNext_1().", Id);
            await DeadlockGrain.CallNext(GrainFactory, callChain, currCallIndex);
        }

        public async Task CallNext_2(List<(long GrainId, bool Blocking)> callChain, int currCallIndex)
        {
            using var _ = RequestContext.AllowCallChainReentrancy();
            this.logger.LogInformation("Inside grain {Id} CallNext_2().", Id);
            await DeadlockGrain.CallNext(GrainFactory, callChain, currCallIndex);
        }
    }

    [Reentrant]
    public class DeadlockReentrantGrain : Grain, IDeadlockReentrantGrain
    {
        private readonly ILogger logger;
        public DeadlockReentrantGrain(ILoggerFactory loggerFactory) => this.logger = loggerFactory.CreateLogger(this.Id);
        private string Id => $"DeadlockReentrantGrain {base.IdentityString}";

        public async Task CallNext_1(List<(long GrainId, bool Blocking)> callChain, int currCallIndex)
        {
            using var _ = RequestContext.AllowCallChainReentrancy();
            this.logger.LogInformation("Inside grain {Id} CallNext_1()", Id);
            await DeadlockGrain.CallNext(GrainFactory, callChain, currCallIndex);
        }

        public async Task CallNext_2(List<(long GrainId, bool Blocking)> callChain, int currCallIndex)
        {
            using var _ = RequestContext.AllowCallChainReentrancy();
            this.logger.LogInformation("Inside grain {Id} CallNext_2()", Id);
            await DeadlockGrain.CallNext(GrainFactory, callChain, currCallIndex);
        }
    }

    public class CallChainReentrancyGrain : Grain, ICallChainReentrancyGrain
    {
        private TaskCompletionSource _unblocker = new();

        private string Id => this.GetPrimaryKeyString();

        public async Task CallChain(ICallChainObserver observer, List<(string TargetGrain, ReentrancyCallType CallType)> callChain, int callIndex)
        {
            await observer.OnEnter(Id, callIndex);
            try
            {
                if (callChain.Count == 0)
                {
                    return;
                }

                var op = callChain[0];
                var target = GrainFactory.GetGrain<ICallChainReentrancyGrain>(op.TargetGrain);
                var newChain = callChain.Skip(1).ToList();
                var nextCallIndex = callIndex + 1;
                switch (op.CallType)
                {
                    case ReentrancyCallType.Regular:
                        await Task.WhenAny(_unblocker.Task, target.CallChain(observer, newChain, nextCallIndex));
                        break;
                    case ReentrancyCallType.AllowCallChainReentrancy:
                        {
                            using var _ = RequestContext.AllowCallChainReentrancy();
                            await Task.WhenAny(_unblocker.Task, target.CallChain(observer, newChain, nextCallIndex));
                            break;
                        }

                    case ReentrancyCallType.SuppressCallChainReentrancy:
                        {
                            using var _ = RequestContext.SuppressCallChainReentrancy();
                            await Task.WhenAny(_unblocker.Task, target.CallChain(observer, newChain, nextCallIndex));
                            break;
                        }
                }
            }
            finally
            {
                await observer.OnExit(Id, callIndex);
            }
        }

        public Task UnblockWaiters()
        {
            _unblocker?.SetResult();
            _unblocker = new();
            return Task.CompletedTask;
        }
    }
}
