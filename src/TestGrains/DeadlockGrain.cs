using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    internal class DeadlockGrain
    {
        internal static Task CallNext(IGrainFactory grainFactory, List<Tuple<long, bool>> callChain, int currCallIndex)
        {
            if (currCallIndex >= callChain.Count) return TaskDone.Done;
            Tuple<long, bool> next = callChain[currCallIndex];
            bool call_1 = (currCallIndex % 2) == 1; // odd (1) call 1, even (zero) - call 2.
            if (next.Item2)
            {
                IDeadlockNonReentrantGrain nextGrain = grainFactory.GetGrain<IDeadlockNonReentrantGrain>(next.Item1);
                if (call_1)
                    return nextGrain.CallNext_1(callChain, currCallIndex + 1);
                else
                    return nextGrain.CallNext_2(callChain, currCallIndex + 1);
            }
            else
            {
                IDeadlockReentrantGrain nextGrain = grainFactory.GetGrain<IDeadlockReentrantGrain>(next.Item1);
                if (call_1)
                    return nextGrain.CallNext_1(callChain, currCallIndex + 1);
                else
                    return nextGrain.CallNext_2(callChain, currCallIndex + 1);
            }
        }
    }

    public class DeadlockNonReentrantGrain : Grain, IDeadlockNonReentrantGrain
    {
        private string Id { get { return String.Format("DeadlockNonReentrantGrain {0}", base.IdentityString); } }

        public Task CallNext_1(List<Tuple<long, bool>> callChain, int currCallIndex)
        {
            GetLogger(Id).Info("Inside grain {0} CallNext_1().", Id);
            return DeadlockGrain.CallNext(GrainFactory, callChain, currCallIndex);
        }

        public Task CallNext_2(List<Tuple<long, bool>> callChain, int currCallIndex)
        {
            GetLogger(Id).Info("Inside grain {0} CallNext_2().", Id);
            return DeadlockGrain.CallNext(GrainFactory, callChain, currCallIndex);
        }
    }

    [Reentrant]
    public class DeadlockReentrantGrain : Grain, IDeadlockReentrantGrain
    {
        private string Id { get { return String.Format("DeadlockReentrantGrain {0}", base.IdentityString); } }

        public Task CallNext_1(List<Tuple<long, bool>> callChain, int currCallIndex)
        {
            GetLogger(Id).Info("Inside grain {0} CallNext_1()", Id);
            return DeadlockGrain.CallNext(GrainFactory, callChain, currCallIndex);
        }

        public Task CallNext_2(List<Tuple<long, bool>> callChain, int currCallIndex)
        {
            GetLogger(Id).Info("Inside grain {0} CallNext_2()", Id);
            return DeadlockGrain.CallNext(GrainFactory, callChain, currCallIndex);
        }
    }
}
