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
        /// <param name="instrumentationKey">The instrumentation key for New Relic.</param>
        public static ISiloHostBuilder AddNewRelicTelemetryConsumer(this ISiloHostBuilder hostBuilder, string instrumentationKey = null)
        {
            return hostBuilder.ConfigureServices((context, services) => ConfigureServices(context, services, instrumentationKey));
        }

        /// <summary>
        /// Adds a metrics telemetric consumer provider of type <see cref="NRTelemetryConsumer"/>.
        /// </summary>
        /// <param name="clientBuilder"></param>
        /// <param name="instrumentationKey">The instrumentation key for New Relic.</param>
        public static IClientBuilder AddNewRelicTelemetryConsumer(this IClientBuilder clientBuilder, string instrumentationKey = null)
        {
            return clientBuilder.ConfigureServices((context, services) => ConfigureServices(context, services, instrumentationKey));
        }

        private static void ConfigureServices(HostBuilderContext context, IServiceCollection services, string instrumentationKey)
        {
            services.ConfigureFormatter<NewRelicTelemetryConsumerOptions>();
            services.Configure<TelemetryOptions>(options => options.AddConsumer<NRTelemetryConsumer>());
            if (!string.IsNullOrWhiteSpace(instrumentationKey))
                services.Configure<NewRelicTelemetryConsumerOptions>(options => options.InstrumentationKey = instrumentationKey);
        }

    }
}
