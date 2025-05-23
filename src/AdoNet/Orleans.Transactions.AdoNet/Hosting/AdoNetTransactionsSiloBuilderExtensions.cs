using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.AdoNet.Entity;
using Orleans.Transactions.AdoNet.Storage;
using Orleans.Transactions.AdoNet.TransactionalState;
using Orleans.Transactions.AdoNet.Utils;

namespace Orleans.Transactions.AdoNet.Hosting
{
    public static class AdoNetTransactionsSiloBuilderExtensions
    {
        public static ISiloBuilder AddAdoNetTransactionalStateStorageAsDefault(
            this ISiloBuilder builder,
            Action<TransactionalStateStorageOptions> configureOptions = null)
        {
            return builder.AddAdoNetTransactionalStateStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        public static ISiloBuilder AddAdoNetTransactionalStateStorage(
            this ISiloBuilder builder,
            string name,
            Action<TransactionalStateStorageOptions> configureOptions = null)
        {
            return builder.AddAdoNetTransactionalStateStorage(name, (OptionsBuilder<TransactionalStateStorageOptions> optionsBuilder) => optionsBuilder.Configure<IServiceProvider>((options, services) =>
            {
                configureOptions.Invoke(options);
                //now just oracle sqlparameter dot is different
                if (options.Invariant == AdoNetInvariants.InvariantNameOracleDatabase
                    && string.IsNullOrWhiteSpace(options.SqlParameterDot))
                {
                    options.SqlParameterDot = Constants.OracleParameterDot;
                }
                options.InitExecuteSqlDic();
            }));
        }
    }

    /// <summary>
    /// <see cref="IServiceCollection"/> extensions.
    /// </summary>
    internal static class AdoNetTransactionServicecollectionExtensions
    {
        internal static ISiloBuilder AddAdoNetTransactionalStateStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<TransactionalStateStorageOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddAdoNetTransactionalStateStorage(name, configureOptions));
        }

        internal static IServiceCollection AddAdoNetTransactionalStateStorage(this IServiceCollection services, string name,
            Action<OptionsBuilder<TransactionalStateStorageOptions>> configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<TransactionalStateStorageOptions>(name));

            services.TryAddSingleton<ITransactionalStateStorageFactory>(sp => sp.GetKeyedService<ITransactionalStateStorageFactory>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            services.AddKeyedSingleton<ITransactionalStateStorageFactory>(name, (sp, key) => TransactionalStateStorageFactory.Create(sp, key as string));
            services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(s => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredKeyedService<ITransactionalStateStorageFactory>(name));

            return services;
        }
    }

    /// <summary>
    ///
    /// </summary>
    internal static class ExecuteSqlExtensions
    {
        /// <summary>
        /// init sqldic
        /// </summary>
        /// <param name="options"></param>
        internal static void InitExecuteSqlDic(this TransactionalStateStorageOptions options)
        {
            var queryKeySql = ActionToSql.QuerySimpleSql(options.KeyEntityTableName, new List<string>()
            {
                nameof(KeyEntity.StateId),nameof(KeyEntity.CommittedSequenceId),
                nameof(KeyEntity.Metadata),nameof(KeyEntity.ETag)
            },
                new List<string>()
            {
                nameof(KeyEntity.StateId)
            }, null, options.SqlParameterDot);
            options.ExecuteSqlDcitionary.Add(Constants.QueryKeySql,queryKeySql);

            var addKeySql = ActionToSql.InsertSql(options.KeyEntityTableName, new List<string>() {
                nameof(KeyEntity.StateId),nameof(KeyEntity.CommittedSequenceId),
                nameof(KeyEntity.Metadata),nameof(KeyEntity.Timestamp),nameof(KeyEntity.ETag) }, options.SqlParameterDot);
            options.ExecuteSqlDcitionary.Add(Constants.AddKeySql, addKeySql);

            string updateKeySql = ActionToSql.UpdateSql(options.KeyEntityTableName, new List<string>()
            {
                 nameof(KeyEntity.CommittedSequenceId),nameof(KeyEntity.Metadata),nameof(KeyEntity.Timestamp)
            }, new List<string>() {
                nameof(KeyEntity.StateId),nameof(KeyEntity.ETag)
            }, options.SqlParameterDot);
            options.ExecuteSqlDcitionary.Add(Constants.UpdateKeySql, updateKeySql);

            string delKeySql = ActionToSql.DeleteSql(options.KeyEntityTableName, new List<string>()
            {
                nameof(KeyEntity.StateId),nameof(KeyEntity.ETag)
            }, options.SqlParameterDot);
            options.ExecuteSqlDcitionary.Add(Constants.DelKeySql, delKeySql);

            string queryStateSql = ActionToSql.QuerySimpleSql(options.StateEntityTableName, new List<string>() {
                nameof(StateEntity.StateId), nameof(StateEntity.SequenceId),
                nameof(StateEntity.TransactionId),nameof(StateEntity.TransactionTimestamp),
                nameof(StateEntity.TransactionManager),nameof(StateEntity.SateData),
                nameof(StateEntity.ETag) }, new List<string>()
            {
                nameof(StateEntity.StateId)
            }, new List<string>()
            {
                nameof(StateEntity.SequenceId)
            }, options.SqlParameterDot);
            options.ExecuteSqlDcitionary.Add(Constants.QueryStateSql, queryStateSql);

            string addStateSql = ActionToSql.InsertSql(options.StateEntityTableName, new List<string>() {
                nameof(StateEntity.StateId), nameof(StateEntity.SequenceId),
                nameof(StateEntity.TransactionId),nameof(StateEntity.TransactionTimestamp),
                nameof(StateEntity.TransactionManager),nameof(StateEntity.SateData),
                nameof(StateEntity.ETag), nameof(StateEntity.Timestamp)
            }, options.SqlParameterDot);
            options.ExecuteSqlDcitionary.Add(Constants.AddStateSql, addStateSql);

            string updateStateSql = ActionToSql.UpdateSql(options.StateEntityTableName, new List<string>()
            {
                nameof(StateEntity.TransactionId),nameof(StateEntity.TransactionTimestamp),
                nameof(StateEntity.TransactionManager), nameof(StateEntity.SateData),
                 nameof(StateEntity.Timestamp) },
             new List<string>() {
                nameof(StateEntity.StateId),nameof(StateEntity.SequenceId) }
             , options.SqlParameterDot);
            options.ExecuteSqlDcitionary.Add(Constants.UpdateStateSql, updateStateSql);

            string delStateSql = ActionToSql.DeleteSql(options.StateEntityTableName, new List<string>()
            {
                nameof(StateEntity.StateId),nameof(StateEntity.SequenceId),nameof(StateEntity.ETag)
            }, options.SqlParameterDot);
            options.ExecuteSqlDcitionary.Add(Constants.DelStateSql, delStateSql);
        }
    }
}
