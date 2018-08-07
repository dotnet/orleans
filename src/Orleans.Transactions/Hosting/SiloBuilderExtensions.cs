using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure cluster to use the distributed TM algorithm
        /// </summary>
        /// <param name="builder">Silo host builder</param>
        /// <param name="withStatisticsReporter">Configure a transaction statistics reporter.  Set to false if you want to configure your own transaction statistics reporting or don't want transaction statistics reported</param>
        /// <returns></returns>
        public static ISiloHostBuilder UseDistributedTM(this ISiloHostBuilder builder, bool withStatisticsReporter = true)
        {
            return builder.ConfigureServices(services => services.UseDistributedTM(withStatisticsReporter))
                          .AddGrainExtension<ITransactionManagerExtension, TransactionManagerExtension>()
                          .AddGrainExtension<ITransactionalResourceExtension, TransactionalResourceExtension>();
        }

        internal static IServiceCollection UseDistributedTM(this IServiceCollection services, bool withReporter)
        {
            services.TryAddSingleton<IClock,Clock>();
            services.TryAddSingleton<ITransactionAgentStatistics, TransactionAgentStatistics>();
            services.TryAddSingleton<ITransactionOverloadDetector,TransactionOverloadDetector>();
            services.AddSingleton<ITransactionAgent, TransactionAgent>();
            services.TryAddSingleton(typeof(ITransactionDataCopier<>), typeof(DefaultTransactionDataCopier<>));
            services.AddSingleton<IAttributeToFactoryMapper<TransactionalStateAttribute>, TransactionalStateAttributeMapper>();
            services.TryAddTransient<ITransactionalStateFactory, TransactionalStateFactory>();
            services.TryAddTransient<INamedTransactionalStateStorageFactory, NamedTransactionalStateStorageFactory>();
            services.AddTransient(typeof(ITransactionalState<>), typeof(TransactionalState<>));
            if (withReporter)
                services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, TransactionAgentStatisticsReporter>();
            return services;
        }
    }
}
