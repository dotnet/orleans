using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.TelemetryConsumers.AI;

namespace Orleans.Hosting
{
    public static class AITelemetryConsumerConfigurationExtensions
    {
        /// <summary>
        /// Adds a metrics telemetric consumer provider of type <see cref="AITelemetryConsumer"/>.
        /// </summary>
        /// <param name="hostBuilder"></param>
        /// <param name="telemetryConfiguration">The Application Insights TelemetryConfiguration.</param>
        /// <param name="instrumentationKey">The Application Insights instrumentation key.</param>
        public static ISiloBuilder AddApplicationInsightsTelemetryConsumer(this ISiloBuilder hostBuilder, TelemetryConfiguration telemetryConfiguration = null, string instrumentationKey = null)
        {
            return hostBuilder.ConfigureServices((context, services) => ConfigureServices(context, services, telemetryConfiguration, instrumentationKey));
        }

        /// <summary>
        /// Adds a metrics telemetric consumer provider of type <see cref="AITelemetryConsumer"/>.
        /// </summary>
        /// <param name="clientBuilder"></param>
        /// <param name="telemetryConfiguration">The Application Insights TelemetryConfiguration.</param>
        /// <param name="instrumentationKey">The Application Insights instrumentation key.</param>
        public static IClientBuilder AddApplicationInsightsTelemetryConsumer(this IClientBuilder clientBuilder, TelemetryConfiguration telemetryConfiguration = null, string instrumentationKey = null)
        {
            return clientBuilder.ConfigureServices((context, services) => ConfigureServices(context, services, telemetryConfiguration, instrumentationKey));
        }

        private static void ConfigureServices(Microsoft.Extensions.Hosting.HostBuilderContext context, IServiceCollection services, TelemetryConfiguration telemetryConfiguration, string instrumentationKey)
        {
            services.ConfigureFormatter<ApplicationInsightsTelemetryConsumerOptions>();
            services.Configure<TelemetryOptions>(options => options.AddConsumer<AITelemetryConsumer>());
            if (telemetryConfiguration != null)
                services.Configure<ApplicationInsightsTelemetryConsumerOptions>(options => options.TelemetryConfiguration = telemetryConfiguration);
            if (!string.IsNullOrWhiteSpace(instrumentationKey))
                services.Configure<ApplicationInsightsTelemetryConsumerOptions>(options => options.InstrumentationKey = instrumentationKey);
        }

        private static void ConfigureServices(HostBuilderContext context, IServiceCollection services, TelemetryConfiguration telemetryConfiguration, string instrumentationKey)
        {
            services.ConfigureFormatter<ApplicationInsightsTelemetryConsumerOptions>();
            services.Configure<TelemetryOptions>(options => options.AddConsumer<AITelemetryConsumer>());
            if (telemetryConfiguration != null)
                services.Configure<ApplicationInsightsTelemetryConsumerOptions>(options => options.TelemetryConfiguration = telemetryConfiguration);
            if (!string.IsNullOrWhiteSpace(instrumentationKey))
                services.Configure<ApplicationInsightsTelemetryConsumerOptions>(options => options.InstrumentationKey = instrumentationKey);
        }
    }
}
