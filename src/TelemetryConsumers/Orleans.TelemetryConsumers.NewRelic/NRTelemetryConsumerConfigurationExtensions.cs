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
        public static ISiloBuilder AddNewRelicTelemetryConsumer(this ISiloBuilder hostBuilder)
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

        private static void ConfigureServices(Microsoft.Extensions.Hosting.HostBuilderContext context, IServiceCollection services)
        {
            services.Configure<TelemetryOptions>(options => options.AddConsumer<NRTelemetryConsumer>());
        }

        private static void ConfigureServices(HostBuilderContext context, IServiceCollection services)
        {
            services.Configure<TelemetryOptions>(options => options.AddConsumer<NRTelemetryConsumer>());
        }

    }
}
