using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GrainInterfaces;
using Orleans;

namespace BenchmarkGrains.MapReduce
{
    public abstract class DataflowGrain : Grain, IDataflowGrain
    {
        public Task Complete()
        {
            throw new NotImplementedException();
        }

        public Task Fault()
        {
            throw new NotImplementedException();
        }

        public Task Completion()
        {
            throw new NotImplementedException();
        }
    }
}