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
            return hostBuilder.ConfigureServices(services => ConfigureServices(services));
        }

        /// <summary>
        /// Adds a metrics telemetric consumer provider of type <see cref="NRTelemetryConsumer"/>.
        /// </summary>
        /// <param name="clientBuilder"></param>
        public static IClientBuilder AddNewRelicTelemetryConsumer(this IClientBuilder clientBuilder)
        {
            return clientBuilder.ConfigureServices(services => ConfigureServices(services));
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            services.Configure<TelemetryOptions>(options => options.AddConsumer<NRTelemetryConsumer>());
        }
    }
}
