using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.State;
using Orleans.Configuration;

namespace Orleans.Transactions
{
    /// <summary>
    /// Stateful facet that respects Orleans transaction semantics
    /// </summary>
    public class TransactionalState<TState> : ITransactionalState<TState>, ILifecycleParticipant<IGrainLifecycle>
        where TState : class, new()
    {
        private readonly ITransactionalStateConfiguration config;
        private readonly IGrainActivationContext context;
        private readonly ITransactionDataCopier<TState> copier;
        private readonly IProviderRuntime runtime;
        private readonly IGrainRuntime grainRuntime;
        private readonly ILoggerFactory loggerFactory;
        private readonly JsonSerializerSettings serializerSettings;

        private ILogger logger;
        private ParticipantId participantId;
        private TransactionQueue<TState> queue;

        public string CurrentTransactionId => TransactionContext.GetRequiredTransactionInfo<TransactionInfo>().Id;

        private bool detectReentrancy;

        public TransactionalState(
            ITransactionalStateConfiguration transactionalStateConfiguration, 
            IGrainActivationContext context, 
            ITransactionDataCopier<TState> copier, 
            IProviderRuntime runtime,
            IGrainRuntime grainRuntime,
            ILoggerFactory loggerFactory, 
            JsonSerializerSettings serializerSettings
            )
        {
            this.config = transactionalStateConfiguration;
            this.context = context;
            this.copier = copier;
            this.runtime = runtime;
            this.grainRuntime = grainRuntime;
            this.loggerFactory = loggerFactory;
            this.serializerSettings = serializerSettings;
        }

        /// <summary>
        /// Read the current state.
        /// </summary>
        public Task<TResult> PerformRead<TResult>(Func<TState, TResult> operation)
        {
            if (detectReentrancy)
            {
                throw new LockRecursionException("cannot perform a read operation from within another operation");
            }

            var info = (TransactionInfo)TransactionContext.GetRequiredTransactionInfo<TransactionInfo>();

            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace($"StartRead {info}");

            info.Participants.TryGetValue(this.participantId, out var recordedaccesses);

            // schedule read access to happen under the lock
            return this.queue.RWLock.EnterLock<TResult>(info.TransactionId, info.Priority, recordedaccesses, true,
                 new Task<TResult>(() =>
                 {
                     // check if our record is gone because we expired while waiting
                     if (!this.queue.RWLock.TryGetRecord(info.TransactionId, out TransactionRecord<TState> record))
                     {
                         throw new OrleansTransactionLockAcquireTimeoutException(info.TransactionId.ToString());
                     }

                     // merge the current clock into the transaction time stamp
                     record.Timestamp = this.queue.Clock.MergeUtcNow(info.TimeStamp);

                     if (record.State == null)
                     {
                         this.queue.GetMostRecentState(out record.State, out record.SequenceNumber);
                     }

                     if (logger.IsEnabled(LogLevel.Debug))
                         logger.Debug($"update-lock read v{record.SequenceNumber} {record.TransactionId} {record.Timestamp:o}");

                     // record this read in the transaction info data structure
                     info.RecordRead(this.participantId, record.Timestamp);

                     // perform the read 
                     TResult result = default(TResult);
                     try
                     {
                         detectReentrancy = true;

                         result = operation(record.State);
                     }
                     finally
                     {
                         if (logger.IsEnabled(LogLevel.Trace))
                             logger.Trace($"EndRead {info} {result} {record.State}");

                         detectReentrancy = false;
                     }

                     return result;
                 }));
        }

        /// <inheritdoc/>
        public Task<TResult> PerformUpdate<TResult>(Func<TState, TResult> updateAction)
        {
            if (updateAction == null) throw new ArgumentNullException(nameof(updateAction));
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

            return this.queue.RWLock.EnterLock<TResult>(info.TransactionId, info.Priority, recordedaccesses, false,
                new Task<TResult>(() =>
                {
                    // check if we expired while waiting
                    if (!this.queue.RWLock.TryGetRecord(info.TransactionId, out TransactionRecord<TState> record))
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

                    // record this participant as a TM candidate
                    if (info.TMCandidate.Reference == null || !info.TMCandidate.Equals(this.participantId.Reference))
                    {
                        int batchsize = this.queue.BatchableOperationsCount();
                        if (info.TMCandidate.Reference == null || batchsize > info.TMBatchSize)
                        {
                            info.TMCandidate = this.participantId;
                            info.TMBatchSize = batchsize;
                        }
                    }

                    // perform the write
                    try
                    {
                        detectReentrancy = true;

                        return updateAction(record.State);
                    }
                    finally
                    {
                        if (logger.IsEnabled(LogLevel.Trace))
                            logger.Trace($"EndWrite {info} {record.State}");

                        detectReentrancy = false;
                    }
                }
            ));
        }

        public void Participate(IGrainLifecycle lifecycle)
        {
            lifecycle.Subscribe<TransactionalState<TState>>(GrainLifecycleStage.SetupState, OnSetupState);
        }

        private async Task OnSetupState(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;

            this.participantId = new ParticipantId(this.config.StateName, this.context.GrainInstance.GrainReference);

            this.logger = loggerFactory.CreateLogger($"{context.GrainType.Name}.{this.config.StateName}.{this.context.GrainIdentity.IdentityString}");

            var storageFactory = this.context.ActivationServices.GetRequiredService<INamedTransactionalStateStorageFactory>();
            ITransactionalStateStorage<TState> storage = storageFactory.Create<TState>(this.config.StorageName, this.config.StateName);

            // setup transaction processing pipe
            Action deactivate = () => grainRuntime.DeactivateOnIdle(context.GrainInstance);
            var options = this.context.ActivationServices.GetRequiredService<IOptions<TransactionalStateOptions>>();
            var clock = this.context.ActivationServices.GetRequiredService<IClock>();
            this.queue = new TransactionQueue<TState>(options, this.participantId, deactivate, storage, this.serializerSettings, clock, logger);

            // Add resources factory to the grain context
            this.context.RegisterResourceFactory<ITransactionalResource>(this.config.StateName, () => new TransactionalResource<TState>(this.queue));

            // Add tm factory to the grain context
            this.context.RegisterResourceFactory<ITransactionManager>(this.config.StateName, () => new TransactionManager<TState>(this.queue));

            // recover state
            await this.queue.NotifyOfRestore();
        }
    }
}
