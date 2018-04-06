using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.TelemetryConsumers.NewRelic;

namespace Orleans.Hosting
{
    public static class NRTelemetryConsumerConfigurationExtensions
    {
        /// <summary>
        /// Adds a metrics telemetric consumer provider of type <see cref="NRTelemetryConsumer"/>.
        /// </summary>
        /// <param name="hostBuilder"></param>
        public static ISiloHostBuilder AddNewRelicTelemetryConsumer(this ISiloHostBuilder hostBuilder)
        {
            return hostBuilder.ConfigureServices((context, services) => ConfigureServices(context, services));
        }

        /// <summary>
        /// Adds a metrics telemetric consumer provider of type <see cref="NRTelemetryConsumer"/>.
        /// </summary>
        /// <param name="clientBuilder"></param>
        public static IClientBuilder AddNewRelicTelemetryConsumer(this IClientBuilder clientBuilder)
        {
            return clientBuilder.ConfigureServices((context, services) => ConfigureServices(context, services));
        }

        private static void ConfigureServices(HostBuilderContext context, IServiceCollection services)
        {
            services.Configure<TelemetryOptions>(options => options.AddConsumer<NRTelemetryConsumer>());
        }

    }
}
