using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.TestKit
{
    [Reentrant]
    public partial class ExclusiveLockTransactionTestGrain : Grain, IExclusiveLockTransactionTestGrain
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
                LogInformationSettingValue(this.logger, state.Value, newValue);
                state.Value = newValue;
                LogInformationSetValue(this.logger, state.Value);
            });
        }

        public async Task<int[]> Add(int numberToAdd)
        {
            var result = await data.PerformUpdate(state =>
            {
                LogInformationAddingValue(this.logger, numberToAdd, state.Value);
                state.Value += numberToAdd;
                LogInformationValueAfterAdd(this.logger, numberToAdd, state.Value);
                return state.Value;
            });
            return new int[] { result };
        }

        public async Task<int[]> Get()
        {
            var result = await data.PerformRead(state =>
            {
                LogInformationGetValue(this.logger, state.Value);
                return state.Value;
            });
            return new int[] { result };
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Setting from {Value} to {NewValue}."
        )]
        private static partial void LogInformationSettingValue(ILogger logger, int value, int newValue);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Set to {Value}."
        )]
        private static partial void LogInformationSetValue(ILogger logger, int value);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Adding {NumberToAdd} to value {Value}."
        )]
        private static partial void LogInformationAddingValue(ILogger logger, int numberToAdd, int value);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Value after Adding {NumberToAdd} is {Value}."
        )]
        private static partial void LogInformationValueAfterAdd(ILogger logger, int numberToAdd, int value);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Get {Value}."
        )]
        private static partial void LogInformationGetValue(ILogger logger, int value);
    }
}
