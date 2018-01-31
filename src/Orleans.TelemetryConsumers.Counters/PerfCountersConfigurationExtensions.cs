using OrleansTelemetryConsumers.Counters;

namespace Orleans.Runtime.Configuration
{
    public static class PerfCountersConfigurationExtensions
    {
        /// <summary>
        /// Adds a metrics telemetric consumer provider of type <see cref="OrleansPerfCounterTelemetryConsumer"/>.
        /// </summary>
        /// <param name="config">The cluster configuration object to add the telemetry consumer to.</param>
        public static void AddPerfCountersTelemetryConsumer(this ClientConfiguration config)
        {
            string typeName = typeof(OrleansPerfCounterTelemetryConsumer).FullName;
            string assemblyName = typeof(OrleansPerfCounterTelemetryConsumer).Assembly.GetName().Name;

            config.TelemetryConfiguration.Add(typeName, assemblyName, null);
        }
    }
}
