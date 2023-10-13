using Orleans.Concurrency;
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
