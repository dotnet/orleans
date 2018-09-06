using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.State;
using Orleans.Transactions.TOC;

[assembly: GenerateSerializer(typeof(Orleans.Transactions.TransactionCommitter<>.OperationState))]

namespace Orleans.Transactions
{
    public class TransactionCommitter<TService> : ITransactionCommitter<TService>, ILifecycleParticipant<IGrainLifecycle>
        where TService : class
    {
        private readonly ITransactionCommitterConfiguration config;
        private readonly IGrainActivationContext context;
        private readonly ITransactionDataCopier<OperationState> copier;
        private readonly IProviderRuntime runtime;
        private readonly IGrainRuntime grainRuntime;
        private readonly ILoggerFactory loggerFactory;
        private readonly JsonSerializerSettings serializerSettings;

        private ILogger logger;
        private ParticipantId participantId;
        private TransactionQueue<OperationState> queue;

        private bool detectReentrancy;

        public TransactionCommitter(
            ITransactionCommitterConfiguration config,
            IGrainActivationContext context,
            ITransactionDataCopier<OperationState> copier,
            IProviderRuntime runtime,
            IGrainRuntime grainRuntime,
            ILoggerFactory loggerFactory,
            JsonSerializerSettings serializerSettings
            )
        {
            this.config = config;
            this.context = context;
            this.copier = copier;
            this.runtime = runtime;
            this.grainRuntime = grainRuntime;
            this.loggerFactory = loggerFactory;
            this.serializerSettings = serializerSettings;
        }

        /// <inheritdoc/>
        public Task OnCommit(ITransactionCommitOperation<TService> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            if (detectReentrancy)
            {
                throw new LockRecursionException("cannot perform an update operation from within another operation");
            }

            var info = (TransactionInfo)TransactionContext.GetRequiredTransactionInfo<TransactionInfo>();

            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace($"StartWrite {info}");

            if (info.IsReadOnly)
            {
                throw new OrleansReadOnlyViolatedException(info.Id);
            }

            info.Participants.TryGetValue(this.participantId, out var recordedaccesses);

            return this.queue.RWLock.EnterLock<bool>(info.TransactionId, info.Priority, recordedaccesses, false,
                new Task<bool>(() =>
                {
                    // check if we expired while waiting
                    if (!this.queue.RWLock.TryGetRecord(info.TransactionId, out TransactionRecord<OperationState> record))
                    {
                        throw new OrleansTransactionLockAcquireTimeoutException(info.TransactionId.ToString());
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

                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.Debug($"update-lock write v{record.SequenceNumber} {record.TransactionId} {record.Timestamp:o}");

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
                        if (logger.IsEnabled(LogLevel.Trace))
                            logger.Trace($"EndWrite {info} {record.TransactionId} {record.Timestamp}");

                        detectReentrancy = false;
                    }
                }
            ));
        }

        public void Participate(IGrainLifecycle lifecycle)
        {
            lifecycle.Subscribe<TransactionalState<OperationState>>(GrainLifecycleStage.SetupState, OnSetupState);
        }

        private async Task OnSetupState(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;

            this.participantId = new ParticipantId(this.config.ServiceName, this.context.GrainInstance.GrainReference, ParticipantId.Role.PriorityManager);

            this.logger = loggerFactory.CreateLogger($"{context.GrainType.Name}.{this.config.ServiceName}.{this.context.GrainIdentity.IdentityString}");

            var storageFactory = this.context.ActivationServices.GetRequiredService<INamedTransactionalStateStorageFactory>();
            ITransactionalStateStorage<OperationState> storage = storageFactory.Create<OperationState>(this.config.StorageName, this.config.ServiceName);

            // setup transaction processing pipe
            Action deactivate = () => grainRuntime.DeactivateOnIdle(context.GrainInstance);
            var options = this.context.ActivationServices.GetRequiredService<IOptions<TransactionalStateOptions>>();
            var clock = this.context.ActivationServices.GetRequiredService<IClock>();
            TService service = this.context.ActivationServices.GetRequiredServiceByName<TService>(this.config.ServiceName);
            this.queue = new TocTransactionQueue<TService>(service, options, this.participantId, deactivate, storage, this.serializerSettings, clock, logger);

            // Add transaction manager factory to the grain context
            this.context.RegisterResourceFactory<ITransactionManager>(this.config.ServiceName, () => new TransactionManager<OperationState>(this.queue));

            // recover state
            await this.queue.NotifyOfRestore();
        }

        [Serializable]
        public class OperationState
        {
            public ITransactionCommitOperation<TService> Operation { get; set; }
        }
    }
}
