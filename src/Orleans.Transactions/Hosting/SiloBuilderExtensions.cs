using Orleans.Transactions;
using Orleans.Transactions.Abstractions;

namespace Orleans.Hosting;

/// <summary>
/// Provides transaction configuration extensions for <see cref="ISiloBuilder"/>.
/// </summary>
public static class SiloBuilderExtensions
{
    /// <summary>
    /// Adds Orleans transaction services and transaction protocol grain extensions to the silo.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder UseTransactions(this ISiloBuilder builder)
    {
        return builder.ConfigureServices(services => services.UseTransactionsWithSilo())
                      .AddGrainExtension<ITransactionManagerExtension, TransactionManagerExtension>()
                      .AddGrainExtension<ITransactionalResourceExtension, TransactionalResourceExtension>();
    }
}
