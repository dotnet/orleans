using System.Collections.Generic;
using Orleans.TelemetryConsumers.AI;
using System.Linq;

namespace Orleans.Runtime.Configuration
{
    public static class AITelemetryConsumerConfigurationExtensions
    {
        /// <summary>
        /// Adds a metrics telemetric consumer provider of type <see cref="AITelemetryConsumer"/>.
        /// </summary>
        /// <param name="config">The cluster configuration object to add the telemetry consumer to.</param>
        /// <param name="instrumentationKey">The Application Insights instrumentation key.</param>
        public static void AddPerfCountersTelemetryConsumer(this ClusterConfiguration config, string instrumentationKey)
        {
            string typeName = typeof(AITelemetryConsumer).FullName;
            string assemblyName = typeof(AITelemetryConsumer).Assembly.GetName().Name;

            foreach (var nodeConfig in config.Overrides.Values.Union(new[] { config.Defaults }))
            {
                nodeConfig.TelemetryConfiguration.Add(typeName, assemblyName, new Dictionary<string, object> { { "instrumentationKey", instrumentationKey} });
            }
        }

        /// <summary>
        /// Adds a metrics telemetric consumer provider of type <see cref="AITelemetryConsumer"/>.
        /// </summary>
        /// <param name="config">The cluster configuration object to add the telemetry consumer to.</param>
        /// <param name="instrumentationKey">The Application Insights instrumentation key.</param>
        public static void AddPerfCountersTelemetryConsumer(this ClientConfiguration config, string instrumentationKey)
        {
            string typeName = typeof(AITelemetryConsumer).FullName;
            string assemblyName = typeof(AITelemetryConsumer).Assembly.GetName().Name;

            config.TelemetryConfiguration.Add(typeName, assemblyName, new Dictionary<string, object> { { "instrumentationKey", instrumentationKey } });
        }
    }
}
