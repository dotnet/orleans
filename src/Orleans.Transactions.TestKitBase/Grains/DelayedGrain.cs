using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.TestKit.Base.Grains
{
    public class DelayedGrainState
    {
        public string Value { get; set; }
    }

    public class DelayedGrain : Grain, IDelayedGrain
    {
        private readonly ITransactionalState<DelayedGrainState> data;

        public DelayedGrain([TransactionalState("data", TransactionTestConstants.TransactionStore)]
            ITransactionalState<DelayedGrainState> data)
        {
            this.data = data;
        }

        public Task UpdateState(TimeSpan delay, string newState) =>
            data.PerformUpdate(state =>
            {
                Thread.Sleep(delay);
                state.Value = newState;
            });

        public Task ThrowException() => throw new Exception("this grain explodes");
    }
}