using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.AzureCosmos;
using Orleans.Configuration;

namespace Orleans.Hosting
{
    public static class AzureCosmosReminderExtensions
    {
        /// <summary>
        /// Configures the silo to use Azure Cosmos DB for reminder storage.
        /// </summary>
        public static ISiloBuilder UseAzureCosmosReminderService(this ISiloBuilder builder, Action<AzureCosmosReminderOptions> configure)
            => builder.ConfigureServices(services => Add(services.Configure(configure)));

        /// <summary>
        /// Configures the silo to use Azure Cosmos DB for reminder storage.
        /// </summary>
        public static ISiloBuilder UseAzureCosmosReminderService(this ISiloBuilder builder, Action<OptionsBuilder<AzureCosmosReminderOptions>> configure)
            => builder.ConfigureServices(services =>
            {
                configure(services.AddOptions<AzureCosmosReminderOptions>());
                Add(services);
            });

        private static void Add(IServiceCollection services)
        {
            services.AddSingleton<IReminderTable, AzureCosmosReminderStorage>();
            services.ConfigureFormatter<AzureCosmosReminderOptions>();
        }
    }
}
