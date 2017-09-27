using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Core;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    /// <summary>
    /// Stateful facet that respects Orleans transaction semantics
    /// </summary>
    /// <typeparam name="TState"></typeparam>
    public class TransactionalState<TState> : ITransactionalState<TState>, ITransactionalResource, ILifecycleParticipant<IGrainLifecycle>
        where TState : class, new()
    {
        private readonly ITransactionalStateConfiguration config;
        private readonly IGrainActivationContext context;
        private readonly ITransactionDataCopier<TState> copier;
        private readonly ITransactionAgent transactionAgent;
        private readonly IProviderRuntime runtime;
        private readonly ILogger logger;

        private readonly Dictionary<long, TState> transactionCopy;
        private readonly AsyncSerialExecutor<bool> storageExecutor;

        private IStorage<TransactionalStateRecord<TState>> storage;
        private ITransactionalResource transactionalResource;

        // In-memory version of the persistent state.
        private readonly SortedDictionary<long, LogRecord<TState>> log;
        private TState value;
        private TransactionalResourceVersion version;
        private long stableVersion;
        private bool validState;

        private long writeLowerBound;

        public TState State => GetState();

        public TransactionalState(ITransactionalStateConfiguration transactionalStateConfiguration, IGrainActivationContext context, ITransactionDataCopier<TState> copier, ITransactionAgent transactionAgent, IProviderRuntime runtime, ILoggerFactory loggerFactory)
        {
            this.config = transactionalStateConfiguration;
            this.context = context;
            this.copier = copier;
            this.transactionAgent = transactionAgent;
            this.runtime = runtime;
            this.logger = loggerFactory.CreateLogger($"{this.context.GrainIdentity}+{this.config.StateName}");
            this.transactionCopy = new Dictionary<long, TState>();
            this.storageExecutor = new AsyncSerialExecutor<bool>();
            this.log = new SortedDictionary<long, LogRecord<TState>>();
        }

        /// <summary>
        /// Transactional Write procedure.
        /// </summary>
        public void Save()
        {
            var info = TransactionContext.GetTransactionInfo();

            this.logger.Debug("Write {0}", info);

            if (info.IsReadOnly)
            {
                // For obvious reasons...
                throw new OrleansReadOnlyViolatedException(info.TransactionId);
            }

            Restore();

            var copiedValue = this.transactionCopy[info.TransactionId];

            //
            // Validation
            //

            if (this.version.TransactionId > info.TransactionId || this.writeLowerBound >= info.TransactionId)
            {
                // Prevent cycles. Wait-die
                throw new OrleansTransactionWaitDieException(info.TransactionId);
            }

            TransactionalResourceVersion nextVersion = TransactionalResourceVersion.Create(info.TransactionId,
                this.version.TransactionId == info.TransactionId ? this.version.WriteNumber + 1 : 1);

            //
            // Update Transaction Context
            //
            info.RecordWrite(transactionalResource, this.version, this.stableVersion);

            //
            // Modify the State
            //
            if (!this.log.ContainsKey(info.TransactionId))
            {
                LogRecord<TState> r = new LogRecord<TState>();
                this.log[info.TransactionId] = r;
            }

            LogRecord<TState> logRecord = this.log[info.TransactionId];
            logRecord.NewVal = copiedValue;
            logRecord.Version = nextVersion;
            this.value = copiedValue;
            this.version = nextVersion;

            this.transactionCopy.Remove(info.TransactionId);
        }

        #region lifecycle
        public void Participate(IGrainLifecycle lifecycle)
        {
            lifecycle.Subscribe(GrainLifecycleStage.SetupState, OnSetupState);
        }
        #endregion lifecycle

        #region ITransactionalResource
        async Task<bool> ITransactionalResource.Prepare(long transactionId, TransactionalResourceVersion? writeVersion,
            TransactionalResourceVersion? readVersion)
        {
            this.transactionCopy.Remove(transactionId);

            long wlb = 0;
            if (readVersion.HasValue)
            {
                this.writeLowerBound = Math.Max(this.writeLowerBound, readVersion.Value.TransactionId - 1);
                wlb = this.writeLowerBound;
            }

            if (!ValidateWrite(writeVersion))
            {
                return false;
            }

            if (!ValidateRead(transactionId, readVersion))
            {
                return false;
            }

            try
            {
                // Note that the checks above will need to be done again
                // after we aquire the lock because things could change in the meantime.
                return await this.storageExecutor.AddNext(() => GuardState(() => PersistPrepare(wlb, transactionId, writeVersion, readVersion)));
            }
            catch (Exception ex)
            {
                // On error, queue up a recovery action.  Will do nothing if state recovers before this is processed
                this.storageExecutor.AddNext(() => GuardState(() => Task.FromResult(true))).Ignore();
                this.logger.Error(OrleansTransactionsErrorCode.Transactions_PrepareFailed, $"Prepare of transaction {transactionId} failed.", ex);
                await ((ITransactionalResource)this).Abort(transactionId);
                return false;
            }
        }

        /// <summary>
        /// Implementation of ITransactionalGrain Abort method. See interface documentation for more details.
        /// </summary>
        Task ITransactionalResource.Abort(long transactionId)
        {
            // Rollback t if it has changed the grain
            if (this.log.ContainsKey(transactionId))
            {
                Rollback(transactionId);
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Implementation of ITransactionalGrain Commit method. See interface documentation for more details.
        /// </summary>
        async Task ITransactionalResource.Commit(long transactionId)
        {
            // Learning that t is committed implies that all pending transactions before t also committed
            if (transactionId > this.stableVersion)
            {
                try
                {
                    bool success = await this.storageExecutor.AddNext(() => GuardState(() => PersistCommit(transactionId)));
                } catch(Exception)
                {
                    // On error, queue up a recovery action.  Will do nothing if state recovers before this is processed
                    this.storageExecutor.AddNext(() => GuardState(() => Task.FromResult(true))).Ignore();
                    throw;
                }
            }
        }

        private async Task<bool> GuardState(Func<Task<bool>> action)
        {
            if (!this.validState)
            {
                await this.storage.ReadStateAsync();
                DoRecovery();
            }
            this.validState = false;
            bool results = await action();
            this.validState = true;
            return results;
        }

        private async Task<bool> PersistPrepare(long wlb, long transactionId, TransactionalResourceVersion? writeVersion, TransactionalResourceVersion? readVersion)
        {
            if (!ValidateWrite(writeVersion))
            {
                return false;
            }

            if (!ValidateRead(transactionId, readVersion))
            {
                return false;
            }

            // check if we need to do a log write
            if (this.storage.State.Version.TransactionId >= transactionId && this.storage.State.WriteLowerBound >= wlb)
            {
                // Logs already persisted, nothing to do here
                return true;
            }

            await Persist(this.storage.State.StableVersion, wlb);

            return true;
        }

        private async Task<bool> PersistCommit(long transactionId)
        {
            if (transactionId <= this.storage.State.StableVersion)
            {
                // Transaction commit already persisted.
                return true;
            }

            // Trim the logs to remove old versions. 
            // Note that we try to keep the highest version that is below or equal to the ReadOnlyTransactionId
            // so that we can use it to serve read only transactions.
            long highestKey = transactionId;
            foreach (var key in this.log.Keys)
            {
                if (key > this.transactionAgent.ReadOnlyTransactionId)
                {
                    break;
                }

                highestKey = key;
            }

            if (this.log.Count != 0)
            {
                List<KeyValuePair<long, LogRecord<TState>>> records = this.log.TakeWhile(kvp => kvp.Key < highestKey).ToList();
                records.ForEach(kvp => this.log.Remove(kvp.Key));
            }

            await Persist(transactionId, this.writeLowerBound);
            return true;
        }
        #endregion ITransactionalResource


        /// <summary>
        /// Find the appropriate version of the state to serve for this transaction.
        /// We enforce reads in transaction id order, hence we find the version written by the highest 
        /// transaction less than or equal to this one
        /// </summary>
        private bool TryGetVersion(long transactionId, out TState readState, out TransactionalResourceVersion readVersion)
        {
            readState = this.value;
            readVersion = this.version;
            bool versionAvailable = this.version.TransactionId <= transactionId;

            LogRecord<TState> logRecord = null;
            foreach (KeyValuePair<long, LogRecord<TState>> kvp in this.log)
            {
                if (kvp.Key > transactionId)
                {
                    break;
                }
                logRecord = kvp.Value;
            }

            if (logRecord == null) return versionAvailable;

            readState = logRecord.NewVal;
            readVersion = logRecord.Version;

            return true;
        }

        /// <summary>
        /// Transactional Read procedure.
        /// </summary>
        private TState GetState()
        {

            var info = TransactionContext.GetTransactionInfo();

            this.logger.Debug("Read {0}", info);

            Restore();

            if (this.transactionCopy.TryGetValue(info.TransactionId, out TState state))
            {
                return state;
            }

            if (!TryGetVersion(info.TransactionId, out TState readState, out TransactionalResourceVersion readVersion))
            {
                // This can only happen if old versions are gone due to checkpointing.
                throw new OrleansTransactionVersionDeletedException(info.TransactionId);
            }

            if (info.IsReadOnly && readVersion.TransactionId > this.stableVersion)
            {
                throw new OrleansTransactionUnstableVersionException(info.TransactionId);
            }

            info.RecordRead(transactionalResource, readVersion, this.storage.State.StableVersion);

            writeLowerBound = Math.Max(writeLowerBound, info.TransactionId - 1);

            TState copy = this.copier.DeepCopy(readState);

            if (!info.IsReadOnly)
            {
                this.transactionCopy[info.TransactionId] = copy;
            }

            return copy;
        }

        /// <summary>
        /// Undo writes to restore state to pre transaction value.
        /// </summary>
        private void Rollback(long transactionId)
        {
            List<KeyValuePair<long, LogRecord<TState>>> records = this.log.SkipWhile(kvp => kvp.Key < transactionId).ToList();
            foreach (KeyValuePair<long, LogRecord<TState>> kvp in records)
            {
                this.log.Remove(kvp.Key);
                this.transactionCopy.Remove(kvp.Key);
            }

            if (this.log.Count > 0)
            {
                LogRecord<TState> lastLogRecord = this.log.Values.Last();
                this.version = lastLogRecord.Version;
                this.value = lastLogRecord.NewVal;
            }
            else
            {
                this.version = TransactionalResourceVersion.Create(0, 0);
                this.value = new TState();
            }
        }

        /// <summary>
        /// Check with the transaction agent and rollback any aborted transaction.
        /// </summary>
        private void Restore()
        {
            foreach (var transactionId in this.log.Keys)
            {
                if (transactionId > this.storage.State.StableVersion && transactionAgent.IsAborted(transactionId))
                {
                    Rollback(transactionId);
                    return;
                }
            }
        }

        /// <summary>
        /// Write log in the format needed for the persistence framework and copy to the persistent state interface.
        /// </summary>
        private void RecordInPersistedLog()
        {
            this.storage.State.Logs.Clear();
            foreach (KeyValuePair<long, LogRecord<TState>> kvp in this.log)
            {
                this.storage.State.Logs[kvp.Key] = kvp.Value.NewVal;
            }
        }

        /// <summary>
        /// Read Log from persistent state interface.
        /// </summary>
        private void RevertToPersistedLog()
        {
            this.log.Clear();
            foreach (KeyValuePair<long, TState> kvp in this.storage.State.Logs)
            {
                this.log[kvp.Key] = new LogRecord<TState>
                {
                    NewVal = kvp.Value,
                    Version = TransactionalResourceVersion.Create(kvp.Key, 1)
                };
            }
        }

        private async Task Persist(long newStableVersion, long newWriteLowerBound)
        {
            RecordInPersistedLog();

            // update storage state
            TransactionalStateRecord<TState> storageState = this.storage.State;
            storageState.Value = this.value;
            storageState.Version = this.version;
            storageState.StableVersion = newStableVersion;
            storageState.WriteLowerBound = newWriteLowerBound;

            await this.storage.WriteStateAsync();

            this.stableVersion = newStableVersion;
        }

        private bool ValidateWrite(TransactionalResourceVersion? writeVersion)
        {
            if (!writeVersion.HasValue)
                return true;

            // Validate that we still have all of the transaction's writes.
            return this.log.TryGetValue(writeVersion.Value.TransactionId, out LogRecord<TState> logRecord) && logRecord.Version == writeVersion.Value;
        }

        private bool ValidateRead(long transactionId, TransactionalResourceVersion? readVersion)
        {
            if (!readVersion.HasValue)
                return true;

            foreach (var key in this.log.Keys)
            {
                if (key >= transactionId)
                {
                    break;
                }

                if (key > readVersion.Value.TransactionId && key < transactionId)
                {
                    return false;
                }
            }

            if (readVersion.Value.TransactionId == 0) return readVersion.Value.WriteNumber == 0;
            // If version read by the transaction is lost, return false.
            if (!this.log.TryGetValue(readVersion.Value.TransactionId, out LogRecord<TState> logRecord)) return false;
            // If version is not same it was overridden by the same transaction that originally wrote it.
            return logRecord.Version == readVersion.Value;
        }

        private void DoRecovery()
        {
            TransactionalStateRecord<TState> storageState = this.storage.State;
            this.stableVersion = storageState.StableVersion;
            this.writeLowerBound = storageState.WriteLowerBound;
            this.version = storageState.Version;
            this.value = storageState.Value;
            RevertToPersistedLog();

            // Rollback any known aborted transactions
            Restore();
        }

        private async Task OnSetupState(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;

            Tuple<TransactionalExtension, ITransactionalExtension> boundExtension = await this.runtime.BindExtension<TransactionalExtension, ITransactionalExtension>(() => new TransactionalExtension());
            boundExtension.Item1.Register(this.config.StateName, this);
            this.transactionalResource = boundExtension.Item2.AsTransactionalResource(this.config.StateName);

            // wire up storage provider
            IStorageProvider storageProvider = string.IsNullOrWhiteSpace(this.config.StorageName)
                ? this.context.ActivationServices.GetRequiredService<IStorageProvider>()
                : this.context.ActivationServices.GetServiceByKey<string, IStorageProvider>(this.config.StorageName);
            this.storage = new StateStorageBridge<TransactionalStateRecord<TState>>(StoredName(), this.context.GrainInstance.GrainReference, storageProvider);

            // load inital state
            await this.storage.ReadStateAsync();

            // recover state
            DoRecovery();
            this.validState = true;
        }

        private string StoredName()
        {
            return $"{this.context.GrainInstance.GetType().FullName}-{this.config.StateName}";
        }

        public bool Equals(ITransactionalResource other)
        {
            return transactionalResource.Equals(other);
        }

        public override string ToString()
        {
            return $"{this.context.GrainInstance}.{this.config.StateName}";
        }
    }

    [Serializable]
    public class LogRecord<T>
    {
        public T NewVal { get; set; }
        public TransactionalResourceVersion Version { get; set; }
    }

    [Serializable]
    public class TransactionalStateRecord<TState>
        where TState : class, new()
    {
        // The transactionId of the transaction that wrote the current value
        public TransactionalResourceVersion Version { get; set; }

        // The last known committed version
        public long StableVersion { get; set; }

        // Writes of transactions with Id equal or below this will be rejected
        public long WriteLowerBound { get; set; }

        public SortedDictionary<long, TState> Logs { get; set; } = new SortedDictionary<long, TState>();

        public TState Value { get; set; } = new TState();
    }
}
