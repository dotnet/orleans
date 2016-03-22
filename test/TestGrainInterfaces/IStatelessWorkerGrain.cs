using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IStatelessWorkerGrain : IGrainWithIntegerKey
    {
        Task LongCall();
        Task<Tuple<Guid, List<Tuple<DateTime, DateTime>>>> GetCallStats();
    }
}
