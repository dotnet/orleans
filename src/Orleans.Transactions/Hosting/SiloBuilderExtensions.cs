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
        public static ISiloHostBuilder UseDistributedTM(this ISiloHostBuilder builder)
        {
            return builder.ConfigureServices(services => services.UseDistributedTM());
        }

        /// <summary>
        /// Configure cluster to use the distributed TM algorithm
        /// </summary>
        public static IServiceCollection UseDistributedTM(this IServiceCollection services)
        {
            services.TryAddSingleton<IClock,Clock>();
            services.TryAddSingleton<TransactionAgentStatistics>();
            services.TryAddSingleton<ITransactionOverloadDetector,TransactionOverloadDetector>();
            services.AddSingleton<ITransactionAgent, TransactionAgent>();
            services.TryAddSingleton(typeof(ITransactionDataCopier<>), typeof(DefaultTransactionDataCopier<>));
            services.AddSingleton<IAttributeToFactoryMapper<TransactionalStateAttribute>, TransactionalStateAttributeMapper>();
            services.TryAddTransient<ITransactionalStateFactory, TransactionalStateFactory>();
            services.TryAddTransient<INamedTransactionalStateStorageFactory, NamedTransactionalStateStorageFactory>();
            services.AddTransient(typeof(ITransactionalState<>), typeof(TransactionalState<>));
            return services;
        }
    }
}
