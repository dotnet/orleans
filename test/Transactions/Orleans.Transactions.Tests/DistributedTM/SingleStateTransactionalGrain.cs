using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.Tests.DistributedTM
{
    [Serializable]
    public class GrainData
    {
        public int Value { get; set; }
    }

    public class SingleStateTransactionalGrain : Grain, ITransactionTestGrain
    {
        private readonly Orleans.Transactions.DistributedTM.ITransactionalState<GrainData> data;
        private readonly ILoggerFactory loggerFactory;
        private ILogger logger;

        public SingleStateTransactionalGrain(
            [TransactionalState("data", TransactionTestConstants.TransactionStore)]
            Orleans.Transactions.DistributedTM.ITransactionalState<GrainData> data,
            ILoggerFactory loggerFactory)
        {
            this.data = data;
            this.loggerFactory = loggerFactory;
        }

        public override Task OnActivateAsync()
        {
            this.logger = this.loggerFactory.CreateLogger(this.GetGrainIdentity().ToString());
            return base.OnActivateAsync();
        }

        public Task Set(int newValue)
        {
            return this.data.PerformUpdate(state =>
            {
                this.logger.LogInformation($"Setting from {state.Value} to {newValue}.");
                state.Value = newValue;
                this.logger.LogInformation($"Set to {state.Value}.");
            });
        }

        public async Task<int[]> Add(int numberToAdd)
        {
            return new[] { await this.data.PerformUpdate(state =>
            {
                this.logger.LogInformation($"Adding {numberToAdd} to {state.Value}.");
                state.Value += numberToAdd;
                this.logger.LogInformation($"Value after Adding {numberToAdd} is {state.Value}.");
                return state.Value;
            }) };
        }


        public async Task<int[]> Get()
        {
            return new[] { await this.data.PerformRead(state =>
            {
                this.logger.LogInformation($"Get {state.Value}.");
                return state.Value;
            }) };
        }

        public async Task AddAndThrow(int numberToAdd)
        {
            await Add(numberToAdd);
            throw new Exception($"{GetType().Name} test exception");
        }

        public Task Deactivate()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }
}
