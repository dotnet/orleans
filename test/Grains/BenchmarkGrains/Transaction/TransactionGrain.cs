using Orleans.Transactions.Abstractions;
using BenchmarkGrainInterfaces.Transaction;

namespace BenchmarkGrains.Transaction
{
    [Serializable]
    [GenerateSerializer]
    public class Info
    {
        [Id(0)]
        public int Value { get; set; }
    }

    public class TransactionGrain : Grain, ITransactionGrain
    {
        private readonly ITransactionalState<Info> info;

        public TransactionGrain(
            [TransactionalState("Info")] ITransactionalState<Info> info)
        {
            this.info = info ?? throw new ArgumentNullException(nameof(info));
        }

        public Task Run()
        {
            return this.info.PerformUpdate(s => s.Value += 1);
        }
    }
}
