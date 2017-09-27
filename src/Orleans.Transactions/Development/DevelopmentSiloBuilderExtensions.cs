using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace Orleans.Transactions.Development
{
    public static class DevelopmentSiloBuilderExtensions
    {
        /// <summary>
        /// Configure cluster to use an in-memory transaction log.
        /// For development and test purposes only
        /// </summary>
        public static ISiloBuilder UseInMemoryTransactionLog(this ISiloBuilder builder)
        {
            return builder.ConfigureServices(UseInMemoryTransactionLog);
        }

        private static void UseInMemoryTransactionLog(IServiceCollection services)
        {
            services.AddTransient(InMemoryTransactionLogStorage.Create);
        }
    }
}
