using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Configuration for telemetry consumers.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class TelemetryConfiguration
    {
        /// <summary>
        /// Configuration for an individual telemetry consumer.
        /// </summary>
        [Serializable]
        [GenerateSerializer]
        public struct ConsumerConfiguration
        {
            /// <summary>
            /// Gets the consumer type.
            /// </summary>
            [Id(1)]
            public Type ConsumerType { get; set; }

            /// <summary>
            /// Gets the configuration properties for this provider instance, as name-value pairs.
            /// </summary>
            [Id(2)]
            public IReadOnlyDictionary<string, object> Properties { get; set; }
        }

        /// <summary>
        /// Gets the collection of telemetry consumer configuration.
        /// </summary>
        [Id(1)]
        public IList<ConsumerConfiguration> Consumers { get; private set; } = new List<ConsumerConfiguration>();

        /// <summary>
        /// Adds a new telemetry consumer configuration.
        /// </summary>
        /// <param name="typeName">The consumer type name.</param>
        /// <param name="assemblyName">The consumer assembly name.</param>
        /// <param name="properties">The key-value pair configuration properties for the consumer.</param>
        /// <exception cref="TypeLoadException">The specified type could not be loaded.</exception>
        /// <exception cref="InvalidOperationException">The telemetry consumer type does not implement <see cref="ITelemetryConsumer"/>.</exception>
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

        /// <summary>
        /// Returns a copy of this instance.
        /// </summary>
        /// <returns>A copy of this instance.</returns>
        public TelemetryConfiguration Clone()
        {
            var config = new TelemetryConfiguration();
            config.Consumers = this.Consumers.ToList();
            return config;
        }
    }
}
