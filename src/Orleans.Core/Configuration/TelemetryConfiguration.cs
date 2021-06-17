using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Orleans.Runtime.Configuration
{
    [Serializable]
    [GenerateSerializer]
    public class TelemetryConfiguration
    {
        [Serializable]
        [GenerateSerializer]
        public struct ConsumerConfiguration
        {
            [Id(1)]
            public Type ConsumerType { get; set; }

            /// <summary>
            /// Configuration properties for this provider instance, as name-value pairs.
            /// </summary>
            [Id(2)]
            public IReadOnlyDictionary<string, object> Properties { get; set; }
        }

        [Id(1)]
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

            if (!typeof(ITelemetryConsumer).IsAssignableFrom(pluginType)) throw new InvalidOperationException($"Telemetry consumer class {typeName} must implement one of {nameof(ITelemetryConsumer)} based interfaces");

            var extendedProperties = properties?.ToDictionary(x => x.Key, x => x.Value);
            Consumers.Add(new ConsumerConfiguration { ConsumerType = pluginType, Properties = extendedProperties });
        }

        public TelemetryConfiguration Clone()
        {
            var config = new TelemetryConfiguration();
            config.Consumers = this.Consumers.ToList();
            return config;
        }
    }
}
