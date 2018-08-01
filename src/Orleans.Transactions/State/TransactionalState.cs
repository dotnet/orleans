using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Newtonsoft.Json;
using Orleans.Transactions.Abstractions.Extensions;
using Orleans.Transactions.State;
using System;
using System.Collections.Generic;
using Orleans.Configuration;
using Microsoft.Extensions.Options;

namespace Orleans.Transactions
{
    /// <summary>
    /// Stateful facet that respects Orleans transaction semantics
    /// </summary>
    public class TransactionalState<TState> : ITransactionalState<TState>, ITransactionParticipant, ILifecycleParticipant<IGrainLifecycle>
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
        private ITransactionParticipant thisParticipant;
        private TransactionQueue<TState> queue;
        private TransactionalResource<TState> resource;
        private TransactionManager<TState> transactionManager;

        private string stateName;
        private string StateName => stateName ?? (stateName = StoredName());

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

        public Task Prepare(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ITransactionParticipant transactionManager)
        {
            return this.resource.Prepare(transactionId, accessCount, timeStamp, transactionManager);
        }

        public Task Abort(Guid transactionId)
        {
            return this.resource.Abort(transactionId);
        }

        public Task Cancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status)
        {
            return this.resource.Cancel(transactionId, timeStamp, status);
        }

        public Task Confirm(Guid transactionId, DateTime timeStamp)
        {
            return this.resource.Confirm(transactionId, timeStamp);
        }

        public Task<TransactionalStatus> CommitReadOnly(Guid transactionId, AccessCounter accessCount, DateTime timeStamp)
        {
            return this.transactionManager.CommitReadOnly(transactionId, accessCount, timeStamp);
        }

        public Task<TransactionalStatus> PrepareAndCommit(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ITransactionParticipant> writeParticipants, int totalParticipants)
        {
            return this.transactionManager.PrepareAndCommit(transactionId, accessCount, timeStamp, writeParticipants, totalParticipants);
        }

        public Task Prepared(Guid transactionId, DateTime timeStamp, ITransactionParticipant participant, TransactionalStatus status)
        {
            return this.transactionManager.Prepared(transactionId,  timeStamp, participant, status);
        }

        public Task Ping(Guid transactionId, DateTime timeStamp, ITransactionParticipant participant)
        {
            return this.transactionManager.Ping(transactionId, timeStamp, participant);
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

            info.Participants.TryGetValue(this.thisParticipant, out var recordedaccesses);

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
                     info.RecordRead(this.thisParticipant, record.Timestamp);

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

            info.Participants.TryGetValue(this.thisParticipant, out var recordedaccesses);

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
                    info.RecordWrite(this.thisParticipant, record.Timestamp);

                    // record this participant as a TM candidate
                    if (info.TMCandidate != resource)
                    {
                        int batchsize = this.queue.BatchableOperationsCount();
                        if (info.TMCandidate == null || batchsize > info.TMBatchSize)
                        {
                            info.TMCandidate = this.thisParticipant;
                            info.TMBatchSize = batchsize;
                        }
                    }

                    // perform the write
                    TResult result = default(TResult);
                    try
                    {
                        detectReentrancy = true;

                        if (updateAction != null)
                        {
                            result = updateAction(record.State);
                        }
                        return result;
                    }
                    finally
                    {
                        if (logger.IsEnabled(LogLevel.Trace))
                            logger.Trace($"EndWrite {info} {result} {record.State}");

                        detectReentrancy = false;
                    }
                }
            ));
        }

        public void Participate(IGrainLifecycle lifecycle)
        {
            lifecycle.Subscribe<TransactionalState<TState>>(GrainLifecycleStage.SetupState, OnSetupState);
        }

        public bool Equals(ITransactionParticipant other)
        {
            return thisParticipant.Equals(other);
        }

        public override string ToString()
        {
            return $"{this.context.GrainInstance}.{this.config.StateName}";
        }

        private async Task OnSetupState(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;

            var boundExtension = await this.runtime.BindExtension<TransactionParticipantExtension, ITransactionParticipantExtension>(() => new TransactionParticipantExtension());
            boundExtension.Item1.Register(this.config.StateName, this);
            this.thisParticipant = boundExtension.Item2.AsTransactionParticipant(this.config.StateName);

            this.logger = loggerFactory.CreateLogger($"{context.GrainType.Name}.{this.config.StateName}.{this.thisParticipant.ToShortString()}");

            var storageFactory = this.context.ActivationServices.GetRequiredService<INamedTransactionalStateStorageFactory>();
            ITransactionalStateStorage<TState> storage = storageFactory.Create<TState>(this.config.StorageName, this.config.StateName);

            Action deactivate = () => grainRuntime.DeactivateOnIdle(context.GrainInstance);
            var options = this.context.ActivationServices.GetRequiredService<IOptions<TransactionalStateOptions>>();
            var clock = this.context.ActivationServices.GetRequiredService<IClock>();
            this.queue = new TransactionQueue<TState>(options, this.thisParticipant, deactivate, storage, this.serializerSettings, clock, logger);
            this.resource = new TransactionalResource<TState>(this.queue);
            this.transactionManager = new TransactionManager<TState>(this.queue);

            // recover state
            await this.queue.NotifyOfRestore();
        }

        private string StoredName()
        {
            return $"{this.context.GrainInstance.GetType().FullName}-{this.config.StateName}";
        }
    }
}
