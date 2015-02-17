using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using System.Collections.Generic;
using System;

namespace UnitTestGrains
{
    public interface IStatelessWorkerGrain : IGrain
    {
        Task LongCall();
        Task<Tuple<Guid, List<Tuple<DateTime,DateTime>>>> GetCallStats();
    }
}