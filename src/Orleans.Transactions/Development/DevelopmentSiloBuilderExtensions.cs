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
        public static ISiloHostBuilder UseInMemoryTransactionLog(this ISiloHostBuilder builder)
        {
            return builder.ConfigureServices(services => services.UseInMemoryTransactionLog());
        }

        /// <summary>
        /// Configure cluster to use an in-memory transaction log.
        /// For development and test purposes only
        /// </summary>
        public static IServiceCollection UseInMemoryTransactionLog(this IServiceCollection services)
        {
            return services.AddTransient(InMemoryTransactionLogStorage.Create);
        }
    }
}
