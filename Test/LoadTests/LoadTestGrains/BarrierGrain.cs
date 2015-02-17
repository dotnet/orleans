using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using LoadTestGrainInterfaces;

using Orleans;
using Orleans.Concurrency;

namespace LoadTestGrains
{
    [Reentrant]
    class BarrierGrain : Grain, IBarrierGrain
    {
        private int _releaseAt = -1;
        private HashSet<string> _pollers;

        public override Task OnActivateAsync()
        {
            _pollers = new HashSet<string>();
            return TaskDone.Done;
        }

        public Task<bool> IsReady(int pollerLimit, string pollerId)
        {
            if (pollerLimit < 0)
            {
                throw new ArgumentOutOfRangeException("pollerLimit", pollerLimit, "argument was less than zero");
            }

            if (_releaseAt == -1)
            {
                _releaseAt = pollerLimit;
            }

            _pollers.Add(pollerId);
            
            return Task.FromResult(_pollers.Count >= _releaseAt);
        }
    }
}
