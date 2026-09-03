using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Timers.Internal;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.Diagnostics;
using Orleans.Transactions.State;
using Orleans.Transactions.TOC;

namespace Orleans.Transactions
{
    /// <summary>
    /// Enlists commit operations for a service in the current transaction and applies them after the transaction commits.
    /// </summary>
    /// <typeparam name="TService">The service type which receives committed operations.</typeparam>
    public partial class TransactionCommitter<TService> : ITransactionCommitter<TService>, ILifecycleParticipant<IGrainLifecycle>
        where TService : class
    {
        private readonly ITransactionCommitterConfiguration config;
        private readonly IGrainContext context;
        private readonly ITransactionDataCopier<OperationState> copier;
        private readonly IGrainRuntime grainRuntime;
        private readonly ActivationLifetime activationLifetime;
        private readonly ILogger logger;
        private ParticipantId participantId;
        private TransactionQueue<OperationState> queue = null!;

        private bool detectReentrancy;

        /// <summary>
        /// Initializes a new instance of the <see cref="TransactionCommitter{TService}"/> class.
        /// </summary>
        /// <param name="config">The committer configuration.</param>
        /// <param name="contextAccessor">The accessor for the current grain activation context.</param>
        /// <param name="copier">The copier used to isolate pending commit operations.</param>
        /// <param name="grainRuntime">The grain runtime.</param>
        /// <param name="logger">The logger.</param>
        public TransactionCommitter(
            ITransactionCommitterConfiguration config,
            IGrainContextAccessor contextAccessor,
            ITransactionDataCopier<OperationState> copier,
            IGrainRuntime grainRuntime,
            ILogger<TransactionCommitter<TService>> logger)
        {
            this.config = config;
            this.context = contextAccessor.GrainContext;
            this.copier = copier;
            this.grainRuntime = grainRuntime;
            this.logger = logger;
            this.activationLifetime = new ActivationLifetime(this.context);
        }

        /// <inheritdoc/>
        public Task OnCommit(ITransactionCommitOperation<TService> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            if (detectReentrancy)
            {
                throw new LockRecursionException("cannot perform an update operation from within another operation");
            }

            var info = TransactionContext.GetRequiredTransactionInfo();

            LogTraceStartWrite(info);
            if (info.IsReadOnly)
            {
                throw new OrleansReadOnlyViolatedException(info.Id);
            }

            info.Participants.TryGetValue(this.participantId, out var recordedaccesses);

            return this.queue.RWLock.EnterLock<bool>(info.TransactionId, info.Priority, info.Timeout, recordedaccesses, false, info.UseExclusiveLock,
                () =>
                {
                    // check if we expired while waiting
                    if (!this.queue.RWLock.TryGetRecord(info.TransactionId, out TransactionRecord<OperationState>? record))
                    {
                        throw new OrleansCascadingAbortException(info.TransactionId.ToString());
                    }

                    // merge the current clock into the transaction time stamp
                    record.Timestamp = this.queue.Clock.MergeUtcNow(info.TimeStamp);

                    // link to the latest state
                    if (record.State == null)
                    {
                        this.queue.GetMostRecentState(out record.State, out record.SequenceNumber);
                    }

                    // if this is the first write, make a deep copy of the state
                    if (!record.HasCopiedState)
                    {
                        record.State = this.copier.DeepCopy(record.State);
                        record.SequenceNumber++;
                        record.HasCopiedState = true;
                    }

                    LogDebugUpdateLockWrite(record.SequenceNumber, record.TransactionId, new(record.Timestamp));

                    // record this write in the transaction info data structure
                    info.RecordWrite(this.participantId, record.Timestamp);

                    // perform the write
                    try
                    {
                        detectReentrancy = true;

                        record.State.Operation = operation;
                        return true;
                    }
                    finally
                    {
                        LogTraceEndWrite(info, record.TransactionId, new(record.Timestamp));
                        detectReentrancy = false;
                    }
                }
            );
        }

        /// <inheritdoc/>
        public void Participate(IGrainLifecycle lifecycle)
        {
            lifecycle.Subscribe<TransactionalState<OperationState>>(GrainLifecycleStage.SetupState, OnSetupState);
        }

        private async Task OnSetupState(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;

            this.participantId = new ParticipantId(this.config.ServiceName, this.context.GrainReference, ParticipantId.Role.Resource | ParticipantId.Role.PriorityManager);

            var storageFactory = this.context.ActivationServices.GetRequiredService<INamedTransactionalStateStorageFactory>();
            ITransactionalStateStorage<OperationState> storage = storageFactory.Create<OperationState>(this.config.StorageName, this.config.ServiceName);

            // setup transaction processing pipe
            void deactivate() => grainRuntime.DeactivateOnIdle(context);
            var options = this.context.ActivationServices.GetRequiredService<IOptions<TransactionalStateOptions>>();
            var clock = this.context.ActivationServices.GetRequiredService<IClock>();
            TService service = this.context.ActivationServices.GetRequiredKeyedService<TService>(this.config.ServiceName);
            var timerManager = this.context.ActivationServices.GetRequiredService<ITimerManager>();
            var diagnosticIdentity = new TransactionDiagnosticEvents.TransactionDiagnosticIdentity(
                this.context.Address.SiloAddress,
                this.context.ActivationId);
            this.queue = new TocTransactionQueue<TService>(
                service,
                options,
                this.participantId,
                deactivate,
                storage,
                clock,
                logger,
                timerManager,
                this.activationLifetime,
                diagnosticIdentity);

            // Add transaction manager factory to the grain context
            this.context.RegisterResourceFactory<ITransactionManager>(this.config.ServiceName, () => new TransactionManager<OperationState>(this.queue));

            // recover state
            await this.queue.NotifyOfRestore();
        }

        /// <summary>
        /// Stores the commit operation associated with a transaction record.
        /// </summary>
        [Serializable]
        [GenerateSerializer]
        public sealed class OperationState
        {
            /// <summary>
            /// Gets or sets the operation to apply when the transaction commits.
            /// </summary>
            [Id(0)]
            public ITransactionCommitOperation<TService> Operation { get; set; } = null!;
        }

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "StartWrite {Info}"
        )]
        private partial void LogTraceStartWrite(TransactionInfo info);

        private readonly struct DateTimeLogRecord(DateTime ts)
        {
            public override string ToString() => ts.ToString("o");
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Update-lock write v{SequenceNumber} {TransactionId} {Timestamp}"
        )]
        private partial void LogDebugUpdateLockWrite(long sequenceNumber, Guid transactionId, DateTimeLogRecord timestamp);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "EndWrite {Info} {TransactionId} {Timestamp}"
        )]
        private partial void LogTraceEndWrite(TransactionInfo info, Guid transactionId, DateTimeLogRecord timestamp);
    }
}
