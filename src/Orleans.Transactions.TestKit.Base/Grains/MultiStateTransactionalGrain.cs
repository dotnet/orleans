using Microsoft.Extensions.Logging;
using Orleans.Transactions.Abstractions;
using System;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Represents an integer value stored in transactional grain state.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class GrainData
    {
        /// <summary>
        /// Gets or sets the stored value.
        /// </summary>
        [Id(0)]
        public int Value { get; set; }
    }

    /// <summary>
    /// Implements transaction test operations over the maximum supported number of coordinated states.
    /// </summary>
    public class MaxStateTransactionalGrain : MultiStateTransactionalGrainBaseClass
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MaxStateTransactionalGrain"/> class.
        /// </summary>
        /// <param name="stateFactory">The factory used to create transactional states.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public MaxStateTransactionalGrain(ITransactionalStateFactory stateFactory,
            ILoggerFactory loggerFactory)
            : base(Enumerable.Range(0, TransactionTestConstants.MaxCoordinatedTransactions)
                .Select(i => stateFactory.Create<GrainData>(new TransactionalStateConfiguration(new TransactionalStateAttribute($"data{i}", TransactionTestConstants.TransactionStore))))
                .ToArray(),
                  loggerFactory)
        {
        }
    }

    /// <summary>
    /// Implements transaction test operations over two transactional states.
    /// </summary>
    public class DoubleStateTransactionalGrain : MultiStateTransactionalGrainBaseClass
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DoubleStateTransactionalGrain"/> class.
        /// </summary>
        /// <param name="data1">The first transactional state.</param>
        /// <param name="data2">The second transactional state.</param>
        /// <param name="loggerFactory">The logger factory.</param>
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

    /// <summary>
    /// Implements transaction test operations over one transactional state.
    /// </summary>
    public class SingleStateTransactionalGrain : MultiStateTransactionalGrainBaseClass
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SingleStateTransactionalGrain"/> class.
        /// </summary>
        /// <param name="data">The transactional state.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public SingleStateTransactionalGrain(
            [TransactionalState("data", TransactionTestConstants.TransactionStore)]
            ITransactionalState<GrainData> data,
            ILoggerFactory loggerFactory)
            : base(new ITransactionalState<GrainData>[1] { data }, loggerFactory)
        {
        }
    }

    /// <summary>
    /// Implements transaction test operations without transactional state.
    /// </summary>
    public class NoStateTransactionalGrain : MultiStateTransactionalGrainBaseClass
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="NoStateTransactionalGrain"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory.</param>
        public NoStateTransactionalGrain(
            ILoggerFactory loggerFactory)
            : base(Array.Empty<ITransactionalState<GrainData>>(), loggerFactory)
        {
        }
    }

    /// <summary>
    /// Provides transaction test operations over an ordered collection of integer states.
    /// </summary>
    public partial class MultiStateTransactionalGrainBaseClass : Grain, ITransactionTestGrain
    {
        /// <summary>
        /// The transactional states operated on by this grain.
        /// </summary>
        protected ITransactionalState<GrainData>[] dataArray;
        private readonly ILoggerFactory loggerFactory;

        /// <summary>
        /// The logger for this grain activation.
        /// </summary>
        protected ILogger logger = null!;

        /// <summary>
        /// Initializes a new instance of the <see cref="MultiStateTransactionalGrainBaseClass"/> class.
        /// </summary>
        /// <param name="dataArray">The ordered transactional states.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public MultiStateTransactionalGrainBaseClass(
            ITransactionalState<GrainData>[] dataArray,
            ILoggerFactory loggerFactory)
        {
            this.dataArray = dataArray;
            this.loggerFactory = loggerFactory;
        }

        /// <inheritdoc/>
        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this.logger = this.loggerFactory.CreateLogger(this.GetGrainId().ToString());
            return base.OnActivateAsync(cancellationToken);
        }

        /// <inheritdoc/>
        public async Task Set(int newValue)
        {
            foreach(var data in this.dataArray)
            {
                await data.PerformUpdate(state =>
                {
                    LogInformationSettingValue(this.logger, state.Value, newValue);
                    state.Value = newValue;
                    LogInformationSetValue(this.logger, state.Value);
                });
            }
        }

        /// <inheritdoc/>
        public async Task<int[]> Add(int numberToAdd)
        {
            var result = new int[dataArray.Length];
            for(int i = 0; i < dataArray.Length; i++)
            {
                result[i] = await dataArray[i].PerformUpdate(state =>
                {
                    LogInformationAddingValue(this.logger, numberToAdd, state.Value);
                    state.Value += numberToAdd;
                    LogInformationValueAfterAdd(this.logger, numberToAdd, state.Value);
                    return state.Value;
                });
            }
            return result;
        }

        /// <inheritdoc/>
        public async Task<int[]> Get()
        {
            var result = new int[dataArray.Length];
            for (int i = 0; i < dataArray.Length; i++)
            {
                result[i] = await dataArray[i].PerformRead(state =>
                {
                    LogInformationGetValue(this.logger, state.Value);
                    return state.Value;
                });
            }
            return result;
        }

        /// <inheritdoc/>
        public async Task AddAndThrow(int numberToAdd)
        {
            await Add(numberToAdd);
            throw new AddAndThrowException($"{GetType().Name} test exception");
        }

        /// <inheritdoc/>
        public async Task SetAndThrow(int numberToSet)
        {
            await Set(numberToSet);
            throw new AddAndThrowException($"{GetType().Name} test exception");
        }

        /// <inheritdoc/>
        public Task Deactivate()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
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

    /// <summary>
    /// Represents the intentional failure raised after a transactional state update.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class AddAndThrowException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AddAndThrowException"/> class.
        /// </summary>
        public AddAndThrowException() : base("Unexpected error.") { }

        /// <summary>
        /// Initializes a new instance of the <see cref="AddAndThrowException"/> class with a message.
        /// </summary>
        /// <param name="message">The message describing the failure.</param>
        public AddAndThrowException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="AddAndThrowException"/> class with a message and inner exception.
        /// </summary>
        /// <param name="message">The message describing the failure.</param>
        /// <param name="innerException">The exception which caused this failure.</param>
        public AddAndThrowException(string message, Exception innerException) : base(message, innerException) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="AddAndThrowException"/> class from serialized data.
        /// </summary>
        /// <param name="info">The serialized exception data.</param>
        /// <param name="context">The serialization context.</param>
        [Obsolete]
        protected AddAndThrowException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
