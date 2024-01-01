using BenchmarkGrainInterfaces.MapReduce;

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