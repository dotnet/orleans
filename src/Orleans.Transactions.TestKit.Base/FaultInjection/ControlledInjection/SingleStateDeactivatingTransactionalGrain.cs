using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.TestKit
{

    /// <summary>
    /// Defines transactional operations used to test controlled fault injection.
    /// </summary>
    public interface IFaultInjectionTransactionTestGrain : IGrainWithGuidKey
    {
        /// <summary>
        /// Sets the grain value within a transaction.
        /// </summary>
        /// <param name="newValue">The new value.</param>
        /// <returns>A task representing the operation.</returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task Set(int newValue);

        /// <summary>
        /// Adds to the grain value within a transaction and configures fault injection.
        /// </summary>
        /// <param name="numberToAdd">The value to add.</param>
        /// <param name="faultInjectionControl">The fault to inject, or <see langword="null"/> to perform the operation without a controlled fault.</param>
        /// <returns>A task representing the operation.</returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task Add(int numberToAdd, FaultInjectionControl? faultInjectionControl = null);

        /// <summary>
        /// Gets the grain value within a transaction.
        /// </summary>
        /// <returns>The grain value.</returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task<int> Get();

        /// <summary>
        /// Schedules the grain for deactivation.
        /// </summary>
        /// <returns>A completed task.</returns>
        Task Deactivate();
    }

    /// <summary>
    /// Implements transactional state operations used to test controlled fault injection.
    /// </summary>
    public partial class SingleStateFaultInjectionTransactionalGrain : Grain, IFaultInjectionTransactionTestGrain
    {
        private readonly IFaultInjectionTransactionalState<GrainData> data;
        private readonly ILoggerFactory loggerFactory;
        private ILogger logger = null!;

        /// <summary>
        /// Initializes a new instance of the <see cref="SingleStateFaultInjectionTransactionalGrain"/> class.
        /// </summary>
        /// <param name="data">The fault-injecting transactional state.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public SingleStateFaultInjectionTransactionalGrain(
            [FaultInjectionTransactionalState("data", TransactionTestConstants.TransactionStore)]
            IFaultInjectionTransactionalState<GrainData> data,
            ILoggerFactory loggerFactory)
        {
            this.data = data;
            this.loggerFactory = loggerFactory;
        }

        /// <inheritdoc />
        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this.logger = this.loggerFactory.CreateLogger(this.GetGrainId().ToString());
            LogInformationGrainId(this.logger, this.GetPrimaryKey());

            return base.OnActivateAsync(cancellationToken);
        }

        /// <inheritdoc />
        public Task Set(int newValue)
        {
            return this.data.PerformUpdate(d =>
            {
                LogInformationSettingValue(this.logger, newValue);
                d.Value = newValue;
            });
        }

        /// <inheritdoc />
        public Task Add(int numberToAdd, FaultInjectionControl? faultInjectionControl = null)
        {
            //reset in case control from last tx isn't cleared for some reason
            this.data.FaultInjectionControl.Reset();
            //dont replace it with this.data.FaultInjectionControl = faultInjectionControl, 
            //this.data.FaultInjectionControl must remain the same reference
            if (faultInjectionControl != null)
            {
                this.data.FaultInjectionControl.FaultInjectionPhase = faultInjectionControl.FaultInjectionPhase;
                this.data.FaultInjectionControl.FaultInjectionType = faultInjectionControl.FaultInjectionType;
            }

            return this.data.PerformUpdate(d =>
            {
                LogInformationAddingValue(this.logger, numberToAdd, d.Value);
                d.Value += numberToAdd;
            });
        }

        /// <inheritdoc />
        public Task<int> Get()
        {
            return this.data.PerformRead<int>(d => d.Value);
        }

        /// <inheritdoc />
        public Task Deactivate()
        {
            this.DeactivateOnIdle();
            return Task.CompletedTask;
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "GrainId {GrainId}"
        )]
        private static partial void LogInformationGrainId(ILogger logger, Guid grainId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Setting value {NewValue}."
        )]
        private static partial void LogInformationSettingValue(ILogger logger, int newValue);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Adding {NumberToAdd} to value {Value}."
        )]
        private static partial void LogInformationAddingValue(ILogger logger, int numberToAdd, int value);
    }
}
