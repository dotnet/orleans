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
        public static ISiloHostBuilder UseDistributedTM(this ISiloHostBuilder builder, bool withReporter = true)
        {
            return builder.ConfigureServices(services => services.UseDistributedTM(withReporter));
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
