using Orleans.Concurrency;
using BenchmarkGrainInterfaces.Transaction;

namespace BenchmarkGrains.Transaction
{
    [Reentrant]
    [StatelessWorker]
    public class TransactionRootGrain : Grain, ITransactionRootGrain
    {
        public Task Run(List<int> grains)
        {
            return Task.WhenAll(grains.Select(id => GrainFactory.GetGrain<ITransactionGrain>(id).Run()));
        }
    }
}
