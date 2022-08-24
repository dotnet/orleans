using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Hosting
{
    /// <summary>
    /// <see cref="IServiceCollection"/> extensions.
    /// </summary>
    public static class TransactionsServiceCollectionExtensions
    {
        internal static IServiceCollection UseTransactionsWithSilo(this IServiceCollection services)
        {
            services.AddTransactionsBaseline();
            services.TryAddSingleton(typeof(ITransactionDataCopier<>), typeof(DefaultTransactionDataCopier<>));
            services.AddSingleton<IAttributeToFactoryMapper<TransactionalStateAttribute>, TransactionalStateAttributeMapper>();
            services.TryAddTransient<ITransactionalStateFactory, TransactionalStateFactory>();
            services.AddSingleton<IAttributeToFactoryMapper<TransactionCommitterAttribute>, TransactionCommitterAttributeMapper>();
            services.TryAddTransient<ITransactionCommitterFactory, TransactionCommitterFactory>();
            services.TryAddTransient<INamedTransactionalStateStorageFactory, NamedTransactionalStateStorageFactory>();
            services.AddTransient(typeof(ITransactionalState<>), typeof(TransactionalState<>));
            return services;
        }

        internal static IServiceCollection UseTransactionsWithClient(this IServiceCollection services) => services.AddTransactionsBaseline();

        internal static IServiceCollection AddTransactionsBaseline(this IServiceCollection services)
        {
            services.TryAddSingleton<IClock, Clock>();
            services.AddSingleton<ITransactionAgent, TransactionAgent>();
            services.AddSingleton<ITransactionClient, TransactionClient>();
            services.TryAddSingleton<ITransactionAgentStatistics, TransactionAgentStatistics>();
            services.TryAddSingleton<ITransactionOverloadDetector, TransactionOverloadDetector>();
            return services;
        }
    }
}
