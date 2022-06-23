using Microsoft.Extensions.DependencyInjection;
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
        public static ISiloBuilder UseTransactions(this ISiloBuilder builder, bool withStatisticsReporter = true)
        {
            return builder.ConfigureServices(services => services.UseTransactions(withStatisticsReporter))
                          .AddGrainExtension<ITransactionManagerExtension, TransactionManagerExtension>()
                          .AddGrainExtension<ITransactionalResourceExtension, TransactionalResourceExtension>();
        }
    }
}
