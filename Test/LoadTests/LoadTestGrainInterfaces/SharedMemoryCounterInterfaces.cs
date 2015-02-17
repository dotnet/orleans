using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace LoadTestGrainInterfaces
{
    public interface ISharedMemoryCounterAggregatorGrain : IGrain
    {
        Task Report(long quantity);

        Task<long> Poll();
    }
}
