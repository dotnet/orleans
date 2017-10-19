using Orleans.TelemetryConsumers.NewRelic;
using System.Linq;

namespace Orleans.Runtime.Configuration
{
    public static class NRTelemetryConsumerConfigurationExtensions
    {
        /// <summary>
        /// Adds a metrics telemetric consumer provider of type <see cref="NRTelemetryConsumer"/>.
        /// </summary>
        /// <param name="config">The cluster configuration object to add the telemetry consumer to.</param>
        /// <param name="instrumentationKey">The instrumentation key for New Relic.</param>
        public static void AddPerfCountersTelemetryConsumer(this ClusterConfiguration config, string instrumentationKey)
        {
            string typeName = typeof(NRTelemetryConsumer).FullName;
            string assemblyName = typeof(NRTelemetryConsumer).Assembly.GetName().Name;

            foreach (var nodeConfig in config.Overrides.Values.Union(new[] { config.Defaults }))
            {
                nodeConfig.TelemetryConfiguration.Add(typeName, assemblyName, null);
            }
        }

        /// <summary>
        /// Adds a metrics telemetric consumer provider of type <see cref="NRTelemetryConsumer"/>.
        /// </summary>
        /// <param name="config">The cluster configuration object to add the telemetry consumer to.</param>
        /// <param name="instrumentationKey">The instrumentation key for New Relic.</param>
        public static void AddPerfCountersTelemetryConsumer(this ClientConfiguration config, string instrumentationKey)
        {
            string typeName = typeof(NRTelemetryConsumer).FullName;
            string assemblyName = typeof(NRTelemetryConsumer).Assembly.GetName().Name;

            config.TelemetryConfiguration.Add(typeName, assemblyName, null);
        }
    }
}
