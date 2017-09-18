using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.Development
{
    public static class DevelopmentSiloBuilderExtensions
    {
        /// <summary>
        /// Configure cluster to use an in-cluster transaction manager.
        /// </summary>
        /// <param name="builder"></param>
        /// <returns></returns>
        public static ISiloBuilder UseInMemoryTransactionLog(this ISiloBuilder builder)
        {
            return builder.ConfigureServices(UseInMemoryTransactionLog);
        }

        private static void UseInMemoryTransactionLog(IServiceCollection services)
        {
            services.AddTransient<ITransactionLogStorage, InMemoryTransactionLogStorage>();
        }
    }
}
