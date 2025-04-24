using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.AdoNet.Entity;
using Orleans.Transactions.AdoNet.Storage;
using Orleans.Transactions.AdoNet.Utils;

namespace Orleans.Transactions.AdoNet.TransactionalState
{
    public class TransactionalStateStorageFactory : ITransactionalStateStorageFactory, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly string name;
        private readonly TransactionalStateStorageOptions options;
        private readonly ClusterOptions clusterOptions;
        private readonly JsonSerializerSettings jsonSettings;
        private readonly ILoggerFactory loggerFactory;

        public TransactionalStateStorageFactory(
            string name,
            TransactionalStateStorageOptions options,
            IOptions<ClusterOptions> clusterOptions,
           IServiceProvider services,
           ILoggerFactory loggerFactory)
        {
            this.name = name;
            this.options = options;
            this.clusterOptions = clusterOptions.Value;
            this.jsonSettings = TransactionalStateFactory.GetJsonSerializerSettings(services);
            this.loggerFactory = loggerFactory;
            //now just oracle sqlparameter dot is different
            if (this.options.Invariant == AdoNetInvariants.InvariantNameOracleDatabase
                && string.IsNullOrWhiteSpace(this.options.SqlParameterDot))
            {
                this.options.SqlParameterDot = ":";
            }
            InitExecuteSqlDic();
        }

        public static ITransactionalStateStorageFactory Create(IServiceProvider services, string name)
        {
            var optionsMonitor = services.GetRequiredService<IOptionsMonitor<TransactionalStateStorageOptions>>();
            return ActivatorUtilities.CreateInstance<TransactionalStateStorageFactory>(services, name, optionsMonitor.Get(name));
        }

        public ITransactionalStateStorage<TState> Create<TState>(
            string stateName,
            IGrainContext context) where TState : class, new()
        {
            string partitionKey = MakePartitionKey(context, stateName);
            return ActivatorUtilities.CreateInstance<TransactionalStateStorage<TState>>(context.ActivationServices, partitionKey, this.jsonSettings, this.options);
        }

        private string MakePartitionKey(IGrainContext context, string stateName)
        {
            string grainKey = context.GrainReference.GrainId.ToString();
            var key = $"{grainKey}_{this.clusterOptions.ServiceId}_{stateName}";
            return SanitizePropertyName(key);
        }

        private string SanitizePropertyName(string key)
        {
            key = key
               .Replace('/', '_')        // Forward slash
               .Replace('\\', '_')       // Backslash
               .Replace('#', '_')        // Pound sign
               .Replace('?', '_');       // Question mark

            if (key.Length >= 255)      // the max length of stateId in database
            {
                throw new ArgumentException(string.Format("Key length {0} is too long. Key={1}", key.Length, key));
            }

            return key;
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(OptionFormattingUtilities.Name<TransactionalStateStorageFactory>(name), this.options.InitStage, Init);
        }

        private  Task Init(CancellationToken cancellationToken)
        {
           return Task.CompletedTask;
        }

        /// <summary>
        /// init execute sql
        /// </summary>
        private void InitExecuteSqlDic()
        {
            var addKeySql = ActionToSql.InsertSql(this.options.KeyEntityTableName, new List<string>() {
                nameof(KeyEntity.StateId),nameof(KeyEntity.CommittedSequenceId),
                nameof(KeyEntity.Metadata),nameof(KeyEntity.Timestamp),nameof(KeyEntity.ETag) }, this.options.SqlParameterDot);
            this.options.ExecuteSqlDcitionary.Add(Constants.AddKeySql, addKeySql);

            string updateKeySql = ActionToSql.UpdateSql(this.options.KeyEntityTableName, new List<string>()
            {
                 nameof(KeyEntity.CommittedSequenceId),nameof(KeyEntity.Metadata),nameof(KeyEntity.Timestamp)
            }, new List<string>() {
                nameof(KeyEntity.StateId),nameof(KeyEntity.ETag)
            }, this.options.SqlParameterDot);
            this.options.ExecuteSqlDcitionary.Add(Constants.UpdateKeySql, updateKeySql);

            string delKeySql = ActionToSql.DeleteSql(this.options.KeyEntityTableName, new List<string>()
            {
                nameof(KeyEntity.StateId),nameof(KeyEntity.ETag)
            }, this.options.SqlParameterDot);
            this.options.ExecuteSqlDcitionary.Add(Constants.DelKeySql, delKeySql);

            string addStateSql = ActionToSql.InsertSql(this.options.StateEntityTableName, new List<string>() {
                nameof(StateEntity.StateId), nameof(StateEntity.SequenceId),
                nameof(StateEntity.TransactionId),nameof(StateEntity.TransactionTimestamp),
                nameof(StateEntity.TransactionManager),nameof(StateEntity.StateJson),
                nameof(StateEntity.ETag), nameof(StateEntity.Timestamp)
            }, this.options.SqlParameterDot);
            this.options.ExecuteSqlDcitionary.Add(Constants.AddStateSql, addStateSql);

            string updateStateSql = ActionToSql.UpdateSql(this.options.StateEntityTableName, new List<string>()
            {
                nameof(StateEntity.TransactionId),nameof(StateEntity.TransactionTimestamp),
                nameof(StateEntity.TransactionManager), nameof(StateEntity.StateJson),
                 nameof(StateEntity.Timestamp) },
             new List<string>() {
                nameof(StateEntity.StateId),nameof(StateEntity.SequenceId) }
             , this.options.SqlParameterDot);
            this.options.ExecuteSqlDcitionary.Add(Constants.UpdateStateSql, updateStateSql);

            string delStateSql = ActionToSql.DeleteSql(this.options.StateEntityTableName, new List<string>()
            {
                nameof(StateEntity.StateId),nameof(StateEntity.SequenceId),nameof(StateEntity.ETag)
            }, this.options.SqlParameterDot);
            this.options.ExecuteSqlDcitionary.Add(Constants.DelStateSql, delStateSql);
        }
    }
}
