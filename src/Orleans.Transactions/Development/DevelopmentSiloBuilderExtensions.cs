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
            return builder.ConfigureServices(services => services.UseInMemoryTransactionLog());
        }

        /// TODO: Remove when we move to using silo builder for tests
        #region pre-siloBuilder

        public static void UseInMemoryTransactionLog(this IServiceCollection services)
        {
            services.AddTransient<ITransactionLogStorage, InMemoryTransactionLogStorage>();
        }

        #endregion pre-siloBuilder
    }
}
