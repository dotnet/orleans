using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Newtonsoft.Json;
using Orleans.Transactions;
using Orleans.Transactions.Abstractions.Extensions;

namespace Orleans.Transactions
{
    /// <summary>
    /// Stateful facet that respects Orleans transaction semantics
    /// </summary>
    public partial class TransactionalState<TState> : ITransactionalState<TState>, ITransactionParticipant, ILifecycleParticipant<IGrainLifecycle>
        where TState : class, new()
    {
        private readonly ITransactionalStateConfiguration config;
        private readonly IGrainActivationContext context;
        private readonly ITransactionDataCopier<TState> copier;
        private readonly ITransactionAgent transactionAgent;
        private readonly IProviderRuntime runtime;
        private readonly ILoggerFactory loggerFactory;

        private  ILogger logger;

        private ITransactionParticipant thisParticipant;

        // storage
        private ITransactionalStateStorage<TState> storage;

        private string stateName;
        private string StateName => stateName ?? (stateName = StoredName());

        //private TimeSpan DebuggerAllowance = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromTicks(0);
        private TimeSpan DebuggerAllowance = TimeSpan.FromTicks(0);

        // max time the TM will wait for prepare phase to complete
        private TimeSpan PrepareTimeout => TimeSpan.FromSeconds(20) + DebuggerAllowance;

        // max time a group can occupy the lock
        private TimeSpan LockTimeout => TimeSpan.FromSeconds(8) + DebuggerAllowance;

        // max time a transaction will wait for the lock to become available
        private TimeSpan LockAcquireTimeout => TimeSpan.FromSeconds(10) + DebuggerAllowance;


        private TimeSpan RemoteTransactionPingFrequency => TimeSpan.FromSeconds(60);
        private static TimeSpan ConfirmationRetryDelay => TimeSpan.FromSeconds(30);
        private static int ConfirmationRetryLimit => 3;


        private TState stableState;
        private long stableSequenceNumber;

        // the queues handling the various stages
        private CommitQueue<TState> commitQueue;
        private StorageBatch<TState> storageBatch;

        private Dictionary<Guid, TransactionRecord<TState>> _confirmationTasks;
        private Dictionary<Guid, TransactionRecord<TState>> confirmationTasks
        {
            get
            {
                if (_confirmationTasks == null)
                {
                    _confirmationTasks = new Dictionary<Guid, TransactionRecord<TState>>();
                }
                return _confirmationTasks;
            }
        }

        private TransactionalStatus problemFlag;
        private int failCounter;

        // moves transactions into and out of the lock stage
        private BatchWorker lockWorker;

        // processes storage and post-storage queues, moves transactions out of the commit stage
        private BatchWorker storageWorker;

        // processes confirmation tasks
        private BatchWorker confirmationWorker;

        private CausalClock clock;

        private JsonSerializerSettings serializerSettings;
        // collection tasks
        private Dictionary<DateTime, PMessages> unprocessedPreparedMessages;
        private class PMessages
        {
            public int Count;
            public TransactionalStatus Status;
        }

        public TransactionalState(
            ITransactionalStateConfiguration transactionalStateConfiguration, 
            IGrainActivationContext context, 
            ITransactionDataCopier<TState> copier, 
            ITransactionAgent transactionAgent, 
            IProviderRuntime runtime, 
            ILoggerFactory loggerFactory, 
            JsonSerializerSettings serializerSettings,
            IClock clock
            )
        {
            this.config = transactionalStateConfiguration;
            this.context = context;
            this.copier = copier;
            this.transactionAgent = transactionAgent;
            this.runtime = runtime;
            this.loggerFactory = loggerFactory;
            this.clock = new CausalClock(clock);

            lockWorker = new BatchWorkerFromDelegate(LockWork);
            storageWorker = new BatchWorkerFromDelegate(StorageWork);
            confirmationWorker = new BatchWorkerFromDelegate(ConfirmationWork);

            this.serializerSettings = serializerSettings;
        }

        #region lifecycle

        public void Participate(IGrainLifecycle lifecycle)
        {
            lifecycle.Subscribe<TransactionalState<TState>>(GrainLifecycleStage.SetupState, OnSetupState);
        }

        private async Task OnSetupState(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;

            var boundExtension = await this.runtime.BindExtension<TransactionParticipantExtension, ITransactionParticipantExtension>(() => new TransactionParticipantExtension());
            boundExtension.Item1.Register(this.config.StateName, this);
            this.thisParticipant = boundExtension.Item2.AsTransactionParticipant(this.config.StateName);

            this.logger = loggerFactory.CreateLogger($"{context.GrainType.Name}.{this.config.StateName}.{this.thisParticipant.ToShortString()}");

            var storageFactory = this.context.ActivationServices.GetRequiredService<INamedTransactionalStateStorageFactory>();
            this.storage = storageFactory.Create<TState>(this.config.StorageName, this.config.StateName);

            // recover state
            await Restore();

            storageWorker.Notify();
        }

        #endregion lifecycle
  
        private string StoredName()
        {
            return $"{this.context.GrainInstance.GetType().FullName}-{this.config.StateName}";
        }

        public bool Equals(ITransactionParticipant other)
        {
            return thisParticipant.Equals(other);
        }

        public override string ToString()
        {
            return $"{this.context.GrainInstance}.{this.config.StateName}";
        }

        /// <summary>
        /// called on activation, and when recovering from storage conflicts or other exceptions.
        /// </summary>
        private async Task Restore()
        {
            // start the load
            var loadtask = this.storage.Load();

            // abort active transactions, without waking up waiters just yet
            AbortExecutingTransactions("due to restore");

            // abort all entries in the commit queue
            foreach (var entry in commitQueue.Elements)
            {
                NotifyOfAbort(entry, problemFlag);
            }
            commitQueue.Clear();

            var loadresponse = await loadtask;

            storageBatch = new StorageBatch<TState>(loadresponse, this.serializerSettings);
         

            stableState = loadresponse.CommittedState;
            stableSequenceNumber = loadresponse.CommittedSequenceId;

            if (logger.IsEnabled(LogLevel.Debug))
                logger.Debug($"Load v{stableSequenceNumber} {loadresponse.PendingStates.Count}p {storageBatch.MetaData.CommitRecords.Count}c");

            // ensure clock is consistent with loaded state
            this.clock.Merge(storageBatch.MetaData.TimeStamp);

            // resume prepared transactions (not TM)
            foreach (var pr in loadresponse.PendingStates.OrderBy(ps => ps.TimeStamp))
            {
                if (pr.SequenceId > stableSequenceNumber && pr.TransactionManager != null)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.Debug($"recover two-phase-commit {pr.TransactionId}");
                    var tm = (pr.TransactionManager == null) ? null :
                        (ITransactionParticipant) JsonConvert.DeserializeObject<ITransactionParticipant>(pr.TransactionManager, this.serializerSettings);

                    commitQueue.Add(new TransactionRecord<TState>()
                    {
                        Role = CommitRole.RemoteCommit,
                        TransactionId = Guid.Parse(pr.TransactionId),
                        Timestamp = pr.TimeStamp,
                        State = pr.State,
                        TransactionManager = tm,
                        PrepareIsPersisted = true,
                        LastSent = default(DateTime),
                        ConfirmationResponsePromise = null
                    });
                }
            }

            // resume committed transactions (on TM)
            foreach (var kvp in storageBatch.MetaData.CommitRecords)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.Debug($"recover commit confirmation {kvp.Key}");

                confirmationTasks.Add(kvp.Key, new TransactionRecord<TState>()
                {
                    Role = CommitRole.LocalCommit,
                    TransactionId = kvp.Key,
                    Timestamp = kvp.Value.Timestamp,
                    WriteParticipants = kvp.Value.WriteParticipants
                });
            }

            // clear the problem flag
            problemFlag = TransactionalStatus.Ok;

            // check for work
            confirmationWorker.Notify();
            storageWorker.Notify();
            lockWorker.Notify();
        }
    }
}
