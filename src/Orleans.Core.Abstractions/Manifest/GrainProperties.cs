using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    /// <summary>
    /// Information about a logical grain type <see cref="GrainType"/>.
    /// </summary>
    [Serializable]
    public class GrainProperties
    {
        /// <summary>
        /// Creates a <see cref="GrainProperties"/> instance.
        /// </summary>
        public GrainProperties(ImmutableDictionary<string, string> values)
        {
            this.Properties = values;
        }

        /// <summary>
        /// Gets the properties.
        /// </summary>
        public ImmutableDictionary<string, string> Properties { get; }

        /// <summary>
        /// Returns a detailed string representation of this instance.
        /// </summary>
        public string ToDetailedString()
        {
            if (this.Properties is null) return string.Empty;
            var result = new StringBuilder("[");
            bool first = true;
            foreach (var entry in this.Properties)
            {
                if (!first)
                {
                    result.Append(", ");
                }

                result.Append($"\"{entry.Key}\": \"{entry.Value}\"");
                first = false;
            }
            result.Append("]");

            return result.ToString();
        }
    }

    /// <summary>
    /// Well-known grain properties.
    /// </summary>
    /// <seealso cref="GrainProperties"/>
    public static class WellKnownGrainTypeProperties
    {
        /// <summary>
        /// The name of the placement strategy for grains of this type.
        /// </summary>
        public const string PlacementStrategy = "placement-strategy";

        /// <summary>
        /// The directory policy for grains of this type.
        /// </summary>
        public const string GrainDirectory = "directory-policy";

        /// <summary>
        /// Whether or not messages to this grain are unordered.
        /// </summary>
        public const string Unordered = "unordered";

        /// <summary>
        /// Prefix for keys which indicate <see cref="GrainInterfaceId"/> of interfaces which a grain class implements.
        /// </summary>
        public const string ImplementedInterfacePrefix = "interface.";
    }

    /// <summary>
    /// Provides grain properties.
    /// </summary>
    public interface IGrainPropertiesProvider
    {
        /// <summary>
        /// Adds grain properties to <paramref name="properties"/>.
        /// </summary>
        void Populate(Type grainClass, GrainType grainType, Dictionary<string, string> properties);
    }

    /// <summary>
    /// Interface for <see cref="System.Attribute"/> classes which provide information about a grain.
    /// </summary>
    public interface IGrainPropertiesProviderAttribute
    {
        /// <summary>
        /// Adds grain properties to <paramref name="properties"/>.
        /// </summary>
        void Populate(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties);
    }

    /// <summary>
    /// Provides grain interface properties from attributes implementing <see cref="IGrainPropertiesProviderAttribute"/>.
    /// </summary>
    public sealed class AttributeGrainPropertiesProvider : IGrainPropertiesProvider
    {
        private readonly IServiceProvider serviceProvider;

        /// <summary>
        /// Creates a <see cref="AttributeGrainPropertiesProvider"/> instance.
        /// </summary>
        public AttributeGrainPropertiesProvider(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public void Populate(Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            foreach (var attr in grainClass.GetCustomAttributes(inherit: true))
            {
                if (attr is IGrainPropertiesProviderAttribute providerAttribute)
                {
                    providerAttribute.Populate(this.serviceProvider, grainClass, grainType, properties);
                }
            }
        }
    }
}