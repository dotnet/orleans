using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    /// <summary>
    /// Information about a communication interface.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class GrainInterfaceProperties
    {
        /// <summary>
        /// Creates a <see cref="GrainInterfaceProperties"/> instance.
        /// </summary>
        public GrainInterfaceProperties(ImmutableDictionary<string, string> values)
        {
            this.Properties = values;
        }

        /// <summary>
        /// Gets the properties.
        /// </summary>
        [Id(1)]
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
    /// Well-known grain interface property keys.
    /// </summary>
    /// <seealso cref="GrainInterfaceProperties"/>
    public static class WellKnownGrainInterfaceProperties
    {
        /// <summary>
        /// The version of this interface encoded as a decimal integer.
        /// </summary>
        public const string Version = "version";

        /// <summary>
        /// The encoded <see cref="GrainType"/> corresponding to the primary implementation of an interface.
        /// This is used for resolving a grain type from an interface.
        /// </summary>
        public const string DefaultGrainType = "primary-grain-type";

        /// <summary>
        /// The name of the type of this interface. Used for convention-based matching of primary implementations.
        /// </summary>
        public const string TypeName = "type-name";
    }

    /// <summary>
    /// Provides grain properties.
    /// </summary>
    public interface IGrainInterfacePropertiesProvider
    {
        /// <summary>
        /// Adds grain interface properties to <paramref name="properties"/>.
        /// </summary>
        void Populate(Type interfaceType, GrainInterfaceType grainInterfaceType, Dictionary<string, string> properties);
    }

    /// <summary>
    /// Interface for <see cref="Attribute"/> classes which provide information about a grain interface.
    /// </summary>
    public interface IGrainInterfacePropertiesProviderAttribute
    {
        /// <summary>
        /// Adds grain interface properties to <paramref name="properties"/>.
        /// </summary>
        void Populate(IServiceProvider services, Type interfaceType, Dictionary<string, string> properties);
    }

    /// <summary>
    /// Provides grain interface properties from attributes implementing <see cref="IGrainInterfacePropertiesProviderAttribute"/>.
    /// </summary>
    internal sealed class AttributeGrainInterfacePropertiesProvider : IGrainInterfacePropertiesProvider
    {
        private readonly IServiceProvider serviceProvider;

        /// <summary>
        /// Creates a <see cref="AttributeGrainInterfacePropertiesProvider"/> instance.
        /// </summary>
        public AttributeGrainInterfacePropertiesProvider(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public void Populate(Type interfaceType, GrainInterfaceType grainInterfaceType, Dictionary<string, string> properties)
        {
            foreach (var attr in interfaceType.GetCustomAttributes(inherit: true))
            {
                if (attr is IGrainInterfacePropertiesProviderAttribute providerAttribute)
                {
                    providerAttribute.Populate(this.serviceProvider, interfaceType, properties);
                }
            }
        }
    }

    /// <summary>
    /// Specifies the default grain type to use when constructing a grain reference for this interface without specifying a grain type.
    /// </summary>
    [AttributeUsage(AttributeTargets.Interface, AllowMultiple = false)]
    public sealed class DefaultGrainTypeAttribute : Attribute, IGrainInterfacePropertiesProviderAttribute
    {
        private readonly string grainType;

        /// <summary>
        /// Creates a <see cref="DefaultGrainTypeAttribute"/> instance.
        /// </summary>
        public DefaultGrainTypeAttribute(string grainType)
        {
            this.grainType = grainType;
        }

        /// <inheritdoc />
        void IGrainInterfacePropertiesProviderAttribute.Populate(IServiceProvider services, Type type, Dictionary<string, string> properties)
        {
            properties[WellKnownGrainInterfaceProperties.DefaultGrainType] = this.grainType;
        }
    }
}