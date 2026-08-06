#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    /// <summary>
    /// Information about a communication interface.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable]
    public sealed class GrainInterfaceProperties : IEquatable<GrainInterfaceProperties>
    {
        [NonSerialized]
        private int? _hashCode;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainInterfaceProperties"/> class.
        /// </summary>
        /// <param name="values">
        /// The interface property values.
        /// </param>
        public GrainInterfaceProperties(ImmutableDictionary<string, string> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            EnsureOrdinalKeyComparer(values, nameof(values));
            this.Properties = values.WithComparers(StringComparer.Ordinal, StringComparer.Ordinal);
            Debug.Assert(HasOrdinalComparers(this.Properties));
        }

        /// <summary>
        /// Gets the properties.
        /// </summary>
        [Id(0)]
        public ImmutableDictionary<string, string> Properties { get; }

        /// <summary>
        /// Returns a detailed string representation of this instance.
        /// </summary>
        /// <returns>
        /// A detailed, string representation of this instance.
        /// </returns>
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

        public override bool Equals(object? obj) => obj is GrainInterfaceProperties other && Equals(other);

        public bool Equals(GrainInterfaceProperties? other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;
            return PropertiesEqual(Properties, other.Properties);
        }

        public override int GetHashCode() => _hashCode ??= ComputeHashCode(Properties);

        private static bool PropertiesEqual(ImmutableDictionary<string, string> left, ImmutableDictionary<string, string> right)
        {
            Debug.Assert(HasOrdinalKeyComparer(left));
            Debug.Assert(HasOrdinalKeyComparer(right));
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left.Count != right.Count)
            {
                return false;
            }

            foreach (var entry in left)
            {
                if (!right.TryGetValue(entry.Key, out var value)
                    || !string.Equals(entry.Value, value, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasOrdinalComparers(ImmutableDictionary<string, string> properties)
            => ReferenceEquals(properties.KeyComparer, StringComparer.Ordinal)
                && ReferenceEquals(properties.ValueComparer, StringComparer.Ordinal);

        private static bool HasOrdinalKeyComparer(ImmutableDictionary<string, string> properties)
            => ReferenceEquals(properties.KeyComparer, StringComparer.Ordinal);

        private static void EnsureOrdinalKeyComparer(ImmutableDictionary<string, string> properties, string paramName)
        {
            if (!HasOrdinalKeyComparer(properties))
            {
                throw new ArgumentException("The dictionary must use StringComparer.Ordinal as its key comparer.", paramName);
            }
        }

        private static int ComputeHashCode(ImmutableDictionary<string, string> properties)
        {
            var hash = 0;
            foreach (var entry in properties)
            {
                hash ^= HashCode.Combine(
                    StringComparer.Ordinal.GetHashCode(entry.Key),
                    entry.Value is null ? 0 : StringComparer.Ordinal.GetHashCode(entry.Value));
            }

            return hash;
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
        /// <param name="interfaceType">
        /// The interface type.
        /// </param>
        /// <param name="grainInterfaceType">
        /// The interface type id.
        /// </param>
        /// <param name="properties">
        /// The properties collection which this calls to this method should populate.
        /// </param>
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
        /// <param name="services">
        /// The service provider.
        /// </param>
        /// <param name="interfaceType">
        /// The interface type.
        /// </param>
        /// <param name="properties">
        /// The properties collection which this calls to this method should populate.
        /// </param>
        void Populate(IServiceProvider services, Type interfaceType, Dictionary<string, string> properties);
    }

    /// <summary>
    /// Provides grain interface properties from attributes implementing <see cref="IGrainInterfacePropertiesProviderAttribute"/>.
    /// </summary>
    internal sealed class AttributeGrainInterfacePropertiesProvider : IGrainInterfacePropertiesProvider
    {
        private readonly IServiceProvider serviceProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="AttributeGrainInterfacePropertiesProvider"/> class.
        /// </summary>
        /// <param name="serviceProvider">
        /// The service provider.
        /// </param>
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
        /// Initializes a new instance of the <see cref="DefaultGrainTypeAttribute"/> class.
        /// </summary>
        /// <param name="grainType">
        /// The grain type.
        /// </param>
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
