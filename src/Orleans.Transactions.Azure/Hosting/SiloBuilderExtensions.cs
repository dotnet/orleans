using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Transactions.Abstractions;
using System.Threading.Tasks;

namespace Orleans.Transactions.Azure
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure cluster to use azure transaction log.
        /// </summary>
        public static ISiloBuilder UseAzureTransactionLog(this ISiloBuilder builder, AzureTransactionLogConfiguration config)
        {
            return builder.ConfigureServices(UseAzureTransactionLog)
                          .Configure<AzureTransactionLogConfiguration>((cfg) => cfg.Copy(config));

        }

        private static void UseAzureTransactionLog(IServiceCollection services)
        {
            services.AddTransient(AzureTransactionLogStorage.Create);
        }
    }
}
