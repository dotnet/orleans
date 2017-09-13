using System;
using System.Threading.Tasks;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.Tests
{
    [Serializable]
    public class GrainData
    {
        public int Value { get; set; }
    }

    public class SingleStateTransactionalGrain : Grain, ITransactionTestGrain
    {
        private readonly ITransactionalState<GrainData> data;

        public SingleStateTransactionalGrain(
            [TransactionalState(TransactionTestConstants.TransactionStore)]
            ITransactionalState<GrainData> data)
        {
            this.data = data;
        }

        public Task Set(int newValue)
        {
            this.data.State.Value = newValue;
            this.data.Save();
            return Task.CompletedTask;
        }

        public Task<int> Add(int numberToAdd)
        {
            this.data.State.Value += numberToAdd;
            this.data.Save();
            return Task.FromResult(data.State.Value);
        }

        public Task<int> Get()
        {
            return Task.FromResult<int>(this.data.State.Value);
        }

        public Task<int> AddAndThrow(int numberToAdd)
        {
            this.data.State.Value += numberToAdd;
            this.data.Save();
            throw new Exception($"{GetType().Name} test exception");
        }

        public Task Deactivate()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }
}
