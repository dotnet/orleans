using System.Linq;

namespace Orleans.Runtime.Configuration
{
    public static class PerfCountersConfigurationExtensions
    {
        /// <summary>
        /// Adds a metrics telemetric consumer provider./>.
        /// </summary>
        /// <param name="config">The cluster configuration object to add the telemetry consumer to.</param>
        public static void AddPerfCountersTelemetryConsumer(this ClusterConfiguration config)
        {
            string typeName = " OrleansTelemetryConsumers.Counters.OrleansPerfCounterTelemetryConsumer";
            string assemblyName = "Orleans.TelemetryConsumers.Counters";

            foreach (var nodeConfig in config.Overrides.Values.Union(new[] { config.Defaults }))
            {
                nodeConfig.TelemetryConfiguration.Add(typeName, assemblyName, null);
            }
        }
    }
}
