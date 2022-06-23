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
        internal static IServiceCollection UseTransactions(this IServiceCollection services, bool withReporter)
        {
            services.TryAddSingleton<IClock,Clock>();
            services.TryAddSingleton<ITransactionAgentStatistics, TransactionAgentStatistics>();
            services.TryAddSingleton<ITransactionOverloadDetector,TransactionOverloadDetector>();
            services.AddSingleton<ITransactionAgent, TransactionAgent>();
            services.TryAddSingleton(typeof(ITransactionDataCopier<>), typeof(DefaultTransactionDataCopier<>));
            services.AddSingleton<IAttributeToFactoryMapper<TransactionalStateAttribute>, TransactionalStateAttributeMapper>();
            services.TryAddTransient<ITransactionalStateFactory, TransactionalStateFactory>();
            services.AddSingleton<IAttributeToFactoryMapper<TransactionCommitterAttribute>, TransactionCommitterAttributeMapper>();
            services.TryAddTransient<ITransactionCommitterFactory, TransactionCommitterFactory>();
            services.TryAddTransient<INamedTransactionalStateStorageFactory, NamedTransactionalStateStorageFactory>();
            services.AddTransient(typeof(ITransactionalState<>), typeof(TransactionalState<>));
            if (withReporter)
                services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, TransactionAgentStatisticsReporter>();
            return services;
        }

        internal static IServiceCollection UseTransactions(this IServiceCollection services)
        {
            services.TryAddSingleton<IClock, Clock>();
            services.AddSingleton<ITransactionAgent, TransactionAgent>();
            services.AddSingleton<ITransactionScope, TransactionScope>();
            services.TryAddSingleton<ITransactionAgentStatistics, TransactionAgentStatistics>();
            services.TryAddSingleton<ITransactionOverloadDetector, TransactionOverloadDetector>();
            return services;
        }
    }
}
