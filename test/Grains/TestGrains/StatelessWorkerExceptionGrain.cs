using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;


namespace UnitTests.Grains
{
    [StatelessWorker(MaxLocalWorkers)]
    public class StatelessWorkerExceptionGrain : Grain, IStatelessWorkerExceptionGrain
    {
        public const int MaxLocalWorkers = 1;

        public StatelessWorkerExceptionGrain()
        {
            throw new Exception("oops");
        }

        public Task Ping()
        {
            return Task.CompletedTask;
        }
    }
}
