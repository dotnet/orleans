using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
        private readonly ILoggerFactory loggerFactory;
        private ILogger logger;

        public SingleStateTransactionalGrain(
            [TransactionalState("data", TransactionTestConstants.TransactionStore)]
            ITransactionalState<GrainData> data,
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
            this.logger.LogInformation($"Setting from {this.data.State.Value} to {newValue}.");
            this.data.State.Value = newValue;
            this.data.Save();
            this.logger.LogInformation($"Set to {this.data.State.Value}.");
            return Task.CompletedTask;
        }

        public Task<int> Add(int numberToAdd)
        {
            this.logger.LogInformation($"Adding {numberToAdd} to {this.data.State.Value}.");
            this.data.State.Value += numberToAdd;
            this.data.Save();
            this.logger.LogInformation($"Value after Adding {numberToAdd} is {this.data.State.Value}.");
            return Task.FromResult(data.State.Value);
        }

        public Task<int> Get()
        {
            this.logger.LogInformation($"Get {this.data.State.Value}.");
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
