using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Newtonsoft.Json;
using Orleans.Transactions;

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

        private ITransactionalResource transactionalResource;

        // storage
        private ITransactionalStateStorage<TState> storage;

        // only to be modified at save/load time
        private Metadata metadata;
        private string eTag;
        private bool validState;

        // In-memory version of the persistent state.
        private TState value;
        private readonly SortedDictionary<long, LogRecord<TState>> log;
        private TransactionalResourceVersion version;
        private long highestReadTransactionId;
        private long highCommitTransactionId;

        public TState State => GetState();

        private string stateName;
        private string StateName => stateName ?? (stateName = StoredName());

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

            if (this.version.TransactionId > info.TransactionId || this.highestReadTransactionId >= info.TransactionId)
            {
                // Prevent cycles. Wait-die
                throw new OrleansTransactionWaitDieException(info.TransactionId);
            }

            TransactionalResourceVersion nextVersion = TransactionalResourceVersion.Create(info.TransactionId,
                this.version.TransactionId == info.TransactionId ? this.version.WriteNumber + 1 : 1);

            //
            // Update Transaction Context
            //
            info.RecordWrite(transactionalResource, this.version, this.metadata.StableVersion.TransactionId);

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
                this.highestReadTransactionId = Math.Max(this.highestReadTransactionId, readVersion.Value.TransactionId - 1);
                wlb = this.highestReadTransactionId;
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
            if (transactionId > this.metadata.StableVersion.TransactionId)
            {
                this.highCommitTransactionId = Math.Max(this.highCommitTransactionId, transactionId);
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
                await DoRecovery();
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
            if (this.metadata.StableVersion.TransactionId >= transactionId && this.metadata.HighestReadTransactionId >= wlb)
            {
                // Logs already persisted, nothing to do here
                return true;
            }

            List<PendingTransactionState<TState>> pending = this.log.Select(kvp => new PendingTransactionState<TState>(kvp.Value.Version.ToString(), kvp.Key, kvp.Value.NewVal)).ToList();
            this.metadata.HighestVersion = this.version;
            this.metadata.HighestReadTransactionId = wlb;
            this.eTag = await this.storage.Persist(StateName, this.eTag, this.metadata.ToString(), pending);

            return true;
        }

        private async Task<bool> PersistCommit(long transactionId)
        {
            transactionId = Math.Max(this.highCommitTransactionId, transactionId);
            if (transactionId <= this.metadata.StableVersion.TransactionId)
            {
                // Transaction commit already persisted.
                return true;
            }

            // find version related to this transaction
            TransactionalResourceVersion stableversion = this.log.First(kvp => kvp.Key <= transactionId).Value.Version;

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

            this.metadata.StableVersion = stableversion;
            this.metadata.HighestVersion = this.version;
            this.metadata.HighestReadTransactionId = this.highestReadTransactionId;
            this.eTag = await this.storage.Confirm(StateName, this.eTag, this.metadata.ToString(), stableversion.ToString());

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

            if (info.IsReadOnly && readVersion.TransactionId > this.metadata.StableVersion.TransactionId)
            {
                throw new OrleansTransactionUnstableVersionException(info.TransactionId);
            }

            info.RecordRead(transactionalResource, readVersion, this.metadata.StableVersion.TransactionId);

            this.highestReadTransactionId = Math.Max(this.highestReadTransactionId, info.TransactionId - 1);

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
                if (transactionId > this.metadata.StableVersion.TransactionId && transactionAgent.IsAborted(transactionId))
                {
                    Rollback(transactionId);
                    return;
                }
            }
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

        private async Task DoRecovery()
        {
            // load inital state
            TransactionalStorageLoadResponse<TState> loadResponse = await this.storage.Load(StateName);
            
            this.eTag = loadResponse.ETag;
            this.metadata = Metadata.FromString(loadResponse.Metadata);
            this.highestReadTransactionId = this.metadata.HighestReadTransactionId;
            this.version = this.metadata.HighestVersion;
            this.value = loadResponse.CommittedState;
            this.log.Clear();
            foreach (PendingTransactionState<TState> pendingState in loadResponse.PendingStates)
            {
                this.log[pendingState.SequenceId] = new LogRecord<TState>
                {
                    NewVal = pendingState.State,
                    Version = (TransactionalResourceVersion.TryParse(pendingState.TransactionId, out TransactionalResourceVersion version)) ? version : default(TransactionalResourceVersion)
                };
            }

            // Rollback any known aborted transactions
            Restore();
        }

        private async Task OnSetupState(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;

            Tuple<TransactionalExtension, ITransactionalExtension> boundExtension = await this.runtime.BindExtension<TransactionalExtension, ITransactionalExtension>(() => new TransactionalExtension());
            boundExtension.Item1.Register(this.config.StateName, this);
            this.transactionalResource = boundExtension.Item2.AsTransactionalResource(this.config.StateName);

            INamedTransactionalStateStorageFactory storageFactory = this.context.ActivationServices.GetRequiredService<INamedTransactionalStateStorageFactory>();
            this.storage = storageFactory.Create<TState>(this.config.StorageName);

            // recover state
            await DoRecovery();

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

        private class Metadata
        {
            private static readonly JsonSerializerSettings settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All,
                PreserveReferencesHandling = PreserveReferencesHandling.Objects,
                DateFormatHandling = DateFormatHandling.IsoDateFormat,
                DefaultValueHandling = DefaultValueHandling.Ignore,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore,
                ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
                TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
                Formatting = Formatting.None
            };

            [JsonIgnore]
            public TransactionalResourceVersion HighestVersion { get; set; }
            public string HighestVersionString
            {
                get { return this.HighestVersion.ToString(); }
                set { this.HighestVersion = (TransactionalResourceVersion.TryParse(value, out TransactionalResourceVersion version)) ? version : default(TransactionalResourceVersion); }
            }

            [JsonIgnore]
            public TransactionalResourceVersion StableVersion { get; set; }
            public string StableVersionString
            {
                get { return this.StableVersion.ToString(); }
                set { this.StableVersion = (TransactionalResourceVersion.TryParse(value, out TransactionalResourceVersion version)) ? version : default(TransactionalResourceVersion); }
            }

            public long HighestReadTransactionId { get; set; }

            // TODO consider passing metadata type to storage, and letting storage handle serialization
            #region Serialization
            public override string ToString()
            {
                return JsonConvert.SerializeObject(this, settings);
            }

            public static Metadata FromString(string metadataString)
            {
                return (!string.IsNullOrEmpty(metadataString)) ? JsonConvert.DeserializeObject<Metadata>(metadataString, settings) : new Metadata();
            }
            #endregion
        }

        private class LogRecord<T>
        {
            public T NewVal { get; set; }
            public TransactionalResourceVersion Version { get; set; }
        }
    }
}
