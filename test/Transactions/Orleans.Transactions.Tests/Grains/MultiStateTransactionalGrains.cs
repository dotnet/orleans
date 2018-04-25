using System;
using System.Linq;
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

    public class MaxStateTransactionalGrain : MultiStateTransactionalGrainBaseClass
    {
        public MaxStateTransactionalGrain(
            ITransactionalStateFactory stateFactory,
            ILoggerFactory loggerFactory)
            : base(Enumerable.Range(0, TransactionTestConstants.MaxCoordinatedTransactions)
                .Select(i => stateFactory.Create<GrainData>(new TransactionalStateAttribute($"data{i}", TransactionTestConstants.TransactionStore)))
                .ToArray(), 
                  loggerFactory)
        {
        }
    }

    public class DoubleStateTransactionalGrain : MultiStateTransactionalGrainBaseClass
    {
        public DoubleStateTransactionalGrain(
            [TransactionalState("data1", TransactionTestConstants.TransactionStore)]
            ITransactionalState<GrainData> data1,
            [TransactionalState("data2", TransactionTestConstants.TransactionStore)]
            ITransactionalState<GrainData> data2,
            ILoggerFactory loggerFactory)
            : base(new ITransactionalState<GrainData>[2] { data1, data2 }, loggerFactory)
        {
        }
    }

    public class SingleStateTransactionalGrain : MultiStateTransactionalGrainBaseClass
    {
        public SingleStateTransactionalGrain(
            [TransactionalState("data", TransactionTestConstants.TransactionStore)] ITransactionalState<GrainData> data,
            ILoggerFactory loggerFactory)
            :base(new ITransactionalState<GrainData>[1]{data}, loggerFactory)
        {
        }
    }

    public class MultiStateTransactionalGrainBaseClass: Grain, ITransactionTestGrain
    {
        protected ITransactionalState<GrainData>[] dataArray;
        private readonly ILoggerFactory loggerFactory;
        protected ILogger logger;

        public MultiStateTransactionalGrainBaseClass(
            ITransactionalState<GrainData>[] dataArray,
            ILoggerFactory loggerFactory)
        {
            this.dataArray = dataArray;
            this.loggerFactory = loggerFactory;
        }

        public override Task OnActivateAsync()
        {
            this.logger = this.loggerFactory.CreateLogger(this.GetGrainIdentity().ToString());
            return base.OnActivateAsync();
        }

        public Task Set(int newValue)
        {
            foreach (var data in this.dataArray)
            {
                Set(data, newValue, this.logger);
            }
            return Task.CompletedTask;
        }

        public Task<int[]> Add(int numberToAdd)
        {
            return Task.FromResult(this.dataArray.Select(data => Add(data, numberToAdd, this.logger)).ToArray());
        }

        public Task<int[]> Get()
        {
            return Task.FromResult(this.dataArray.Select(data => Get(data, this.logger)).ToArray());
        }

        public Task AddAndThrow(int numberToAdd)
        {
            foreach (var data in dataArray)
            {
                Add(data, numberToAdd, this.logger);
            }
            throw new Exception($"{GetType().Name} test exception");
        }

        public Task Deactivate()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        private static void Set(ITransactionalState<GrainData> data, int newValue, ILogger logger)
        {
            logger.LogInformation($"Setting from {data.State.Value} to {newValue}.");
            data.State.Value = newValue;
            data.Save();
            logger.LogInformation($"Set to {data.State.Value}.");
        }

        private static int Get(ITransactionalState<GrainData> data, ILogger logger)
        {
            logger.LogInformation($"Get {data.State.Value}.");
            return data.State.Value;
        }

        private static int Add(ITransactionalState<GrainData> data, int numberToAdd, ILogger logger)
        {
            logger.LogInformation($"Adding {numberToAdd} to {data.State.Value}.");
            data.State.Value += numberToAdd;
            data.Save();
            logger.LogInformation($"Value after Adding {numberToAdd} is {data.State.Value}.");
            return data.State.Value;
        }
    }
}
