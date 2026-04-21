using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.TestKit
{
    [Reentrant]
    public class ExclusiveLockTransactionTestGrain : Grain, IExclusiveLockTransactionTestGrain
    {
        private readonly ITransactionalState<GrainData> data;
        private readonly ILoggerFactory loggerFactory;
        protected ILogger logger = null!;

        public ExclusiveLockTransactionTestGrain(
            [TransactionalState("data", TransactionTestConstants.TransactionStore)]
            ITransactionalState<GrainData> data,
            ILoggerFactory loggerFactory)
        {
            this.data = data;
            this.loggerFactory = loggerFactory;
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this.logger = this.loggerFactory.CreateLogger(this.GetGrainId().ToString());
            return base.OnActivateAsync(cancellationToken);
        }

        public async Task Set(int newValue)
        {
            await data.PerformUpdate(state =>
            {
                this.logger.LogInformation("Setting from {Value} to {NewValue}.", state.Value, newValue);
                state.Value = newValue;
                this.logger.LogInformation("Set to {Value}.", state.Value);
            });
        }

        public async Task<int[]> Add(int numberToAdd)
        {
            var result = await data.PerformUpdate(state =>
            {
                this.logger.LogInformation("Adding {NumberToAdd} to value {Value}.", numberToAdd, state.Value);
                state.Value += numberToAdd;
                this.logger.LogInformation("Value after Adding {NumberToAdd} is {Value}.", numberToAdd, state.Value);
                return state.Value;
            });
            return new int[] { result };
        }

        public async Task<int[]> Get()
        {
            var result = await data.PerformRead(state =>
            {
                this.logger.LogInformation("Get {Value}.", state.Value);
                return state.Value;
            });
            return new int[] { result };
        }
    }
}
