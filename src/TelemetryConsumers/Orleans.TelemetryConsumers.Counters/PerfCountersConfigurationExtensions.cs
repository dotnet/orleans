using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using OrleansTelemetryConsumers.Counters;

namespace Orleans.Hosting
{
    public static class PerfCountersConfigurationExtensions
    {
        /// <summary>
        /// Adds a metrics telemetric consumer provider of type <see cref="OrleansPerfCounterTelemetryConsumer"/>.
        /// </summary>
        public static ISiloHostBuilder AddPerfCountersTelemetryConsumer(this ISiloHostBuilder hostBuilder)
        {
            return hostBuilder.ConfigureServices(ConfigureServices);
        }

        /// <summary>
        /// Adds a metrics telemetric consumer provider of type <see cref="OrleansPerfCounterTelemetryConsumer"/>.
        /// </summary>
        public static ISiloBuilder AddPerfCountersTelemetryConsumer(this ISiloBuilder hostBuilder)
        {
            return hostBuilder.ConfigureServices(ConfigureServices);
        }

        /// <summary>
        /// Adds a metrics telemetric consumer provider of type <see cref="OrleansPerfCounterTelemetryConsumer"/>.
        /// </summary>
        public static IClientBuilder AddPerfCountersTelemetryConsumer(this IClientBuilder clientBuilder)
        {
            return clientBuilder.ConfigureServices(ConfigureServices);
        }

        private static void ConfigureServices(Microsoft.Extensions.Hosting.HostBuilderContext context, IServiceCollection services)
        {
            services.Configure<TelemetryOptions>(options => options.AddConsumer<OrleansPerfCounterTelemetryConsumer>());
        }

        private static void ConfigureServices(HostBuilderContext context, IServiceCollection services)
        {
            services.Configure<TelemetryOptions>(options => options.AddConsumer<OrleansPerfCounterTelemetryConsumer>());
        }
    }
}
