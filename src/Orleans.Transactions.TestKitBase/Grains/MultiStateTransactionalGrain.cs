using Microsoft.Extensions.Logging;
using Orleans.Transactions.Abstractions;
using System;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit
{
    [Serializable]
    [GenerateSerializer]
    public class GrainData
    {
        [Id(0)]
        public int Value { get; set; }
    }

    public class MaxStateTransactionalGrain : MultiStateTransactionalGrainBaseClass
    {
        public MaxStateTransactionalGrain(ITransactionalStateFactory stateFactory,
            ILoggerFactory loggerFactory)
            : base(Enumerable.Range(0, TransactionTestConstants.MaxCoordinatedTransactions)
                .Select(i => stateFactory.Create<GrainData>(new TransactionalStateConfiguration(new TransactionalStateAttribute($"data{i}", TransactionTestConstants.TransactionStore))))
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
            [TransactionalState("data", TransactionTestConstants.TransactionStore)]
            ITransactionalState<GrainData> data,
            ILoggerFactory loggerFactory)
            : base(new ITransactionalState<GrainData>[1] { data }, loggerFactory)
        {
        }
    }

    public class NoStateTransactionalGrain : MultiStateTransactionalGrainBaseClass
    {
        public NoStateTransactionalGrain(
            ILoggerFactory loggerFactory)
            : base(Array.Empty<ITransactionalState<GrainData>>(), loggerFactory)
        {
        }
    }

    public class MultiStateTransactionalGrainBaseClass : Grain, ITransactionTestGrain
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
            this.logger = this.loggerFactory.CreateLogger(this.GetGrainId().ToString());
            return base.OnActivateAsync();
        }

        public async Task Set(int newValue)
        {
            foreach(var data in this.dataArray)
            {
                await data.PerformUpdate(state =>
                {
                    this.logger.LogInformation($"Setting from {state.Value} to {newValue}.");
                    state.Value = newValue;
                    this.logger.LogInformation($"Set to {state.Value}.");
                });
            }
        }

        public async Task<int[]> Add(int numberToAdd)
        {
            var result = new int[dataArray.Length];
            for(int i = 0; i < dataArray.Length; i++)
            {
                result[i] = await dataArray[i].PerformUpdate(state =>
                {
                    this.logger.LogInformation($"Adding {numberToAdd} to {state.Value}.");
                    state.Value += numberToAdd;
                    this.logger.LogInformation($"Value after Adding {numberToAdd} is {state.Value}.");
                    return state.Value;
                });
            }
            return result;
        }

        public async Task<int[]> Get()
        {
            var result = new int[dataArray.Length];
            for (int i = 0; i < dataArray.Length; i++)
            {
                result[i] = await dataArray[i].PerformRead(state =>
                {
                    this.logger.LogInformation($"Get {state.Value}.");
                    return state.Value;
                });
            }
            return result;              
        }

        public async Task AddAndThrow(int numberToAdd)
        {
            await Add(numberToAdd);
            throw new AddAndThrowException($"{GetType().Name} test exception");
        }

        public Task Deactivate()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class AddAndThrowException : Exception
    {
        public AddAndThrowException() : base("Unexpected error.") { }

        public AddAndThrowException(string message) : base(message) { }

        public AddAndThrowException(string message, Exception innerException) : base(message, innerException) { }

        protected AddAndThrowException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
