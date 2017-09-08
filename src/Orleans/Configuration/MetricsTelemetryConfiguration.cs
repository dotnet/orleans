using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Orleans.Runtime.Configuration
{
    [Serializable]
    public class MetricsTelemetryConfiguration
    {
        [Serializable]
        public struct ConsumerConfiguration
        {
            public Type ConsumerType { get; set; }

            /// <summary>
            /// Configuration properties for this provider instance, as name-value pairs.
            /// </summary>
            public IReadOnlyDictionary<string, object> Properties { get; set; }
        }

        public IList<ConsumerConfiguration> Consumers { get; private set; } = new List<ConsumerConfiguration>();

        public void Add(string typeName, string assemblyName, IEnumerable<KeyValuePair<string, object>> properties)
        {
            Assembly assembly = null;
            try
            {
                var assemblyRef = new AssemblyName(assemblyName);
                assembly = Assembly.Load(assemblyRef);
            }
            catch (Exception exc)
            {
                throw new TypeLoadException($"Cannot load TelemetryConsumer class {typeName} from assembly {assembly?.FullName ?? assemblyName} - Error={exc}");
            }

            var pluginType = assembly.GetType(typeName);
            if (pluginType == null) throw new TypeLoadException($"Cannot locate plugin class {typeName} in assembly {assembly.FullName}");

            if (!typeof(IMetricTelemetryConsumer).IsAssignableFrom(pluginType)) throw new InvalidOperationException($"MetricsTelemetryConsumer class {typeName} must implement one of {nameof(IMetricTelemetryConsumer)} based interfaces");

            var extendedProperties = properties?.ToDictionary(x => x.Key, x => x.Value);
            Consumers.Add(new ConsumerConfiguration { ConsumerType = pluginType, Properties = extendedProperties });
        }

        public MetricsTelemetryConfiguration Clone()
        {
            var config = new MetricsTelemetryConfiguration();
            config.Consumers = this.Consumers.ToList();
            return config;
        }
    }
}
