using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
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
        private string Id { get { return String.Format("DeadlockNonReentrantGrain {0}", base.IdentityString); } }

        public Task CallNext_1(List<(long GrainId, bool Blocking)> callChain, int currCallIndex)
        {
            this.logger.LogInformation("Inside grain {Id} CallNext_1().", Id);
            return DeadlockGrain.CallNext(GrainFactory, callChain, currCallIndex);
        }

        public Task CallNext_2(List<(long GrainId, bool Blocking)> callChain, int currCallIndex)
        {
            this.logger.LogInformation("Inside grain {Id} CallNext_2().", Id);
            return DeadlockGrain.CallNext(GrainFactory, callChain, currCallIndex);
        }
    }

    [Reentrant]
    public class DeadlockReentrantGrain : Grain, IDeadlockReentrantGrain
    {
        private readonly ILogger logger;
        public DeadlockReentrantGrain(ILoggerFactory loggerFactory) => this.logger = loggerFactory.CreateLogger(this.Id);
        private string Id => $"DeadlockReentrantGrain {base.IdentityString}";

        public Task CallNext_1(List<(long GrainId, bool Blocking)> callChain, int currCallIndex)
        {
            this.logger.LogInformation("Inside grain {Id} CallNext_1()", Id);
            return DeadlockGrain.CallNext(GrainFactory, callChain, currCallIndex);
        }

        public Task CallNext_2(List<(long GrainId, bool Blocking)> callChain, int currCallIndex)
        {
            this.logger.LogInformation("Inside grain {Id} CallNext_2()", Id);
            return DeadlockGrain.CallNext(GrainFactory, callChain, currCallIndex);
        }
    }
}
