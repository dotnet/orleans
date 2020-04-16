using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
        private readonly ILogger<DelayedGrain> logger;
        public DelayedGrain([TransactionalState("data", TransactionTestConstants.TransactionStore)]
            ITransactionalState<DelayedGrainState> data, ILogger<DelayedGrain> logger)
        {
            this.data = data;
            this.logger = logger;
        }

        public async Task UpdateState(TimeSpan delay, string newState)
        {
            logger.LogInformation($"Submitting work on {this.GetPrimaryKey()} with delay {delay}");
            await this.data.PerformUpdate(state =>
            {
                this.logger.LogInformation($"In lock on {this.GetPrimaryKey()}, sleeping for {delay}");
                Thread.Sleep(delay);
                state.Value = newState;
                this.logger.LogInformation($"About to leave lock on {this.GetPrimaryKey()} after {delay}");
            });
        }

        public Task<string> GetState() => this.data.PerformRead(state => state.Value);

        public Task ThrowException() => throw new Exception("this grain explodes");
    }
}