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
        /// <param name="builder"></param>
        /// <returns></returns>
        public static ISiloBuilder UseInClusterTransactionManager(this ISiloBuilder builder, TransactionsConfiguration config)
        {
            return builder.ConfigureServices(services => services.UseInClusterTransactionManager(config));
        }

        public static ISiloBuilder UseTransactionalState(this ISiloBuilder builder)
        {
            return builder.ConfigureServices(services => services.UseTransactionalState());
        }

        /// TODO: Remove when we move to using silo builder for tests
        #region pre-siloBuilder

        public static void UseInClusterTransactionManager(this IServiceCollection services, TransactionsConfiguration config)
        {
            // TODO: Move configuration to container configuration phase, once we move to silo builder in tests.
            services.AddSingleton(config);
            services.AddTransient<TransactionLog>();
            services.AddTransient<ITransactionManager,TransactionManager>();
            services.AddSingleton<TransactionServiceGrainFactory>();
            services.AddSingleton(sp => sp.GetRequiredService<TransactionServiceGrainFactory>().CreateTransactionManagerService());
        }

        public static void UseTransactionalState(this IServiceCollection services)
        {
            // TODO: Move configuration to container configuration phase, once we move to silo builder in tests.
            services.TryAddSingleton(typeof(ITransactionDataCopier<>), typeof(DefaultTransactionDataCopier<>));
            services.AddSingleton<IAttributeToFactoryMapper<TransactionalStateAttribute>, TransactionalStateAttributeMapper>();
            services.TryAddTransient<ITransactionalStateFactory, TransactionalStateFactory>();
            services.AddTransient(typeof(ITransactionalState<>), typeof(TransactionalState<>));
        }

        #endregion pre-siloBuilder
    }
}
