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
using Orleans.Transactions.State;
using Orleans.Transactions.TOC;

namespace Orleans.Transactions
{
    public class TransactionCommitter<TService> : ITransactionCommitter<TService>, ILifecycleParticipant<IGrainLifecycle>
        where TService : class
    {
        private readonly ITransactionCommitterConfiguration config;
        private readonly IGrainContext context;
        private readonly ITransactionDataCopier<OperationState> copier;
        private readonly IGrainRuntime grainRuntime;
        private readonly ActivationLifetime activationLifetime;
        private readonly ILogger logger;
        private ParticipantId participantId;
        private TransactionQueue<OperationState> queue;

        private bool detectReentrancy;

        public TransactionCommitter(
            ITransactionCommitterConfiguration config,
            IGrainContextAccessor contextAccessor,
            ITransactionDataCopier<OperationState> copier,
            IGrainRuntime grainRuntime,
            ILogger<TransactionCommitter<TService>> logger)
        {
            this.config = config;
            context = contextAccessor.GrainContext;
            this.copier = copier;
            this.grainRuntime = grainRuntime;
            this.logger = logger;
            activationLifetime = new ActivationLifetime(context);
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

            if (logger.IsEnabled(LogLevel.Trace))
                logger.LogTrace("StartWrite {Info}", info);

            if (info.IsReadOnly)
            {
                throw new OrleansReadOnlyViolatedException(info.Id);
            }

            info.Participants.TryGetValue(participantId, out var recordedaccesses);

            return queue.RWLock.EnterLock<bool>(info.TransactionId, info.Priority, recordedaccesses, false,
                () =>
                {
                    // check if we expired while waiting
                    if (!queue.RWLock.TryGetRecord(info.TransactionId, out var record))
                    {
                        throw new OrleansCascadingAbortException(info.TransactionId.ToString());
                    }

                    // merge the current clock into the transaction time stamp
                    record.Timestamp = queue.Clock.MergeUtcNow(info.TimeStamp);

                    // link to the latest state
                    if (record.State == null)
                    {
                        queue.GetMostRecentState(out record.State, out record.SequenceNumber);
                    }

                    // if this is the first write, make a deep copy of the state
                    if (!record.HasCopiedState)
                    {
                        record.State = copier.DeepCopy(record.State);
                        record.SequenceNumber++;
                        record.HasCopiedState = true;
                    }

                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug(
                            "Update-lock write v{SequenceNumber} {TransactionId} {Timestamp}",
                            record.SequenceNumber,
                            record.TransactionId,
                            record.Timestamp.ToString("o"));
                    }

                    // record this write in the transaction info data structure
                    info.RecordWrite(participantId, record.Timestamp);

                    // perform the write
                    try
                    {
                        detectReentrancy = true;

                        record.State.Operation = operation;
                        return true;
                    }
                    finally
                    {
                        if (logger.IsEnabled(LogLevel.Trace))
                            logger.LogTrace(
                                "EndWrite {Info} {TransactionId} {Timestamp}",
                                info,
                                record.TransactionId,
                                record.Timestamp);

                        detectReentrancy = false;
                    }
                }
            );
        }

        public void Participate(IGrainLifecycle lifecycle)
        {
            lifecycle.Subscribe<TransactionalState<OperationState>>(GrainLifecycleStage.SetupState, OnSetupState);
        }

        private async Task OnSetupState(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;

            participantId = new ParticipantId(config.ServiceName, context.GrainReference, ParticipantId.Role.Resource | ParticipantId.Role.PriorityManager);

            var storageFactory = context.ActivationServices.GetRequiredService<INamedTransactionalStateStorageFactory>();
            var storage = storageFactory.Create<OperationState>(config.StorageName, config.ServiceName);

            // setup transaction processing pipe
            void deactivate() => grainRuntime.DeactivateOnIdle(context);
            var options = context.ActivationServices.GetRequiredService<IOptions<TransactionalStateOptions>>();
            var clock = context.ActivationServices.GetRequiredService<IClock>();
            var service = context.ActivationServices.GetRequiredServiceByName<TService>(config.ServiceName);
            var timerManager = context.ActivationServices.GetRequiredService<ITimerManager>();
            queue = new TocTransactionQueue<TService>(service, options, participantId, deactivate, storage, clock, logger, timerManager, activationLifetime);

            // Add transaction manager factory to the grain context
            context.RegisterResourceFactory<ITransactionManager>(config.ServiceName, () => new TransactionManager<OperationState>(queue));

            // recover state
            await queue.NotifyOfRestore();
        }

        [Serializable]
        [GenerateSerializer]
        public sealed class OperationState
        {
            [Id(0)]
            public ITransactionCommitOperation<TService> Operation { get; set; }
        }
    }
}
