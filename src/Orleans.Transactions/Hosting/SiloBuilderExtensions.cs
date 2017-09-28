using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Runtime;
using Orleans.Hosting;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure cluster to use an in-cluster transaction manager.
        /// </summary>
        public static ISiloBuilder UseInClusterTransactionManager(this ISiloBuilder builder, TransactionsConfiguration config)
        {
            return builder.ConfigureServices(UseInClusterTransactionManager)
                          .Configure<TransactionsConfiguration>((cfg) => cfg.Copy(config));
        }

        /// <summary>
        /// Configure cluster to support the use of transactional state.
        /// </summary>
        public static ISiloBuilder UseTransactionalState(this ISiloBuilder builder)
        {
            return builder.ConfigureServices(UseTransactionalState);
        }

        private static void UseInClusterTransactionManager(IServiceCollection services)
        {
            services.AddTransient<TransactionLog>();
            services.AddTransient<ITransactionManager,TransactionManager>();
            services.AddSingleton<TransactionServiceGrainFactory>();
            services.AddSingleton(sp => sp.GetRequiredService<TransactionServiceGrainFactory>().CreateTransactionManagerService());
        }

        private static void UseTransactionalState(IServiceCollection services)
        {
            services.TryAddSingleton(typeof(ITransactionDataCopier<>), typeof(DefaultTransactionDataCopier<>));
            services.AddSingleton<IAttributeToFactoryMapper<TransactionalStateAttribute>, TransactionalStateAttributeMapper>();
            services.TryAddTransient<ITransactionalStateFactory, TransactionalStateFactory>();
            services.AddTransient(typeof(ITransactionalState<>), typeof(TransactionalState<>));
        }
    }
}
