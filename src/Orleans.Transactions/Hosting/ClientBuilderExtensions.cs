using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Orleans.Transactions;
using Orleans.Transactions.Abstractions;

namespace Orleans;

public static class ClientBuilderExtensions
{
    public static IClientBuilder UseTransactions(this IClientBuilder builder)
        => builder.ConfigureServices(services => services.UseTransactions());

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
