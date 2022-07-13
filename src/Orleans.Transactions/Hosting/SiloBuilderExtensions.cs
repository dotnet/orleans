using Orleans.Transactions;
using Orleans.Transactions.Abstractions;

namespace Orleans.Hosting;

public static class SiloBuilderExtensions
{
    /// <summary>
    /// Configure cluster to use the distributed TM algorithm
    /// </summary>
    /// <param name="builder">Silo host builder</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder UseTransactions(this ISiloBuilder builder)
    {
        return builder.ConfigureServices(services => services.UseTransactionsWithSilo())
                      .AddGrainExtension<ITransactionManagerExtension, TransactionManagerExtension>()
                      .AddGrainExtension<ITransactionalResourceExtension, TransactionalResourceExtension>();
    }
}
