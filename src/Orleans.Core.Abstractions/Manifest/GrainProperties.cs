using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    /// <summary>
    /// Information about a logical grain type <see cref="GrainType"/>.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class GrainProperties
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GrainProperties"/> class.
        /// </summary>
        /// <param name="values">
        /// The underlying property collection.
        /// </param>
        public GrainProperties(ImmutableDictionary<string, string> values)
        {
            this.Properties = values;
        }

        /// <summary>
        /// Gets the properties.
        /// </summary>
        [Id(1)]
        public ImmutableDictionary<string, string> Properties { get; }

        /// <summary>
        /// Returns a detailed, string representation of this instance.
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
        /// Prefix for keys which indicate <see cref="GrainInterfaceType"/> of interfaces which a grain class implements.
        /// </summary>
        public const string ImplementedInterfacePrefix = "interface.";

        /// <summary>
        /// The period after which an idle grain will be deactivated.
        /// </summary>
        public const string IdleDeactivationPeriod = "idle-duration";

        /// <summary>
        /// The value for <see cref="IdleDeactivationPeriod"/> used to specify that the grain should not be deactivated due to idleness.
        /// </summary>
        public const string IndefiniteIdleDeactivationPeriodValue = "indefinite";

        /// <summary>
        /// The name of the primary implementation type. Used for convention-based matching of primary interface implementations.
        /// </summary>
        public const string TypeName = "type-name";

        /// <summary>
        /// The full name of the primary implementation type. Used for prefix-based matching of implementations.
        /// </summary>
        public const string FullTypeName = "full-type-name";

        /// <summary>
        /// The prefix for binding declarations 
        /// </summary>
        public const string BindingPrefix = "binding";

        /// <summary>
        /// The key for defining a binding type. 
        /// </summary>
        public const string BindingTypeKey = "type";

        /// <summary>
        /// The binding type for Orleans streams.
        /// </summary>
        public const string StreamBindingTypeValue = "stream";

        /// <summary>
        /// The key to specify a stream binding pattern. 
        /// </summary>
        public const string StreamBindingPatternKey = "pattern";

        /// <summary>
        /// The key to specify a stream id mapper 
        /// </summary>
        public const string StreamIdMapperKey = "streamid-mapper";

        /// <summary>
        /// Whether to include the namespace name in the grain id.
        /// </summary>
        public const string StreamBindingIncludeNamespaceKey = "include-namespace";

        /// <summary>
        /// Key type of the grain, if it implement a legacy interface. Valid values are nameof(String), nameof(Int64) and nameof(Guid)
        /// </summary>
        public const string LegacyGrainKeyType = "legacy-grain-key-type";

        /// <summary>
        /// Whether a grain is reentrant or not.
        /// </summary>
        public const string Reentrant = "reentrant";

        /// <summary>
        /// Specifies the name of a method used to determine if a request can interleave other requests.
        /// </summary>
        public const string MayInterleavePredicate = "may-interleave-predicate";
    }

    /// <summary>
    /// Provides grain properties.
    /// </summary>
    public interface IGrainPropertiesProvider
    {
        /// <summary>
        /// Adds grain properties to <paramref name="properties"/>.
        /// </summary>
        /// <param name="grainClass">
        /// The grain class.
        /// </param>
        /// <param name="grainType">
        /// The grain type id.
        /// </param>
        /// <param name="properties">
        /// The properties collection which calls to this method should populate.
        /// </param>
        void Populate(Type grainClass, GrainType grainType, Dictionary<string, string> properties);
    }

    /// <summary>
    /// Interface for <see cref="Attribute"/> classes which provide information about a grain.
    /// </summary>
    public interface IGrainPropertiesProviderAttribute
    {
        /// <summary>
        /// Adds grain properties to <paramref name="properties"/>.
        /// </summary>
        /// <param name="services">
        /// The service provider.
        /// </param>
        /// <param name="grainClass">
        /// The grain class.
        /// </param>
        /// <param name="grainType">
        /// The grain type id.
        /// </param>
        /// <param name="properties">
        /// The properties collection which calls to this method should populate.
        /// </param>
        void Populate(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties);
    }

    /// <summary>
    /// Provides grain interface properties from attributes implementing <see cref="IGrainPropertiesProviderAttribute"/>.
    /// </summary>
    public sealed class AttributeGrainPropertiesProvider : IGrainPropertiesProvider
    {
        private readonly IServiceProvider serviceProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="AttributeGrainPropertiesProvider"/> class.
        /// </summary>
        /// <param name="serviceProvider">
        /// The service provider.
        /// </param>
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

    /// <summary>
    /// Interface for <see cref="Attribute"/> classes which provide information about a grain.
    /// </summary>
    public interface IGrainBindingsProviderAttribute
    {
        /// <summary>
        /// Gets bindings for the type this attribute is attached to.
        /// </summary>
        /// <param name="services">
        /// The service provider.
        /// </param>
        /// <param name="grainClass">
        /// The grain class.
        /// </param>
        /// <param name="grainType">
        /// The grain type.
        /// </param>
        /// <returns>
        /// The bindings for the specified grain.
        /// </returns>
        IEnumerable<Dictionary<string, string>> GetBindings(IServiceProvider services, Type grainClass, GrainType grainType);
    }

    /// <summary>
    /// Provides grain interface properties from attributes implementing <see cref="IGrainPropertiesProviderAttribute"/>.
    /// </summary>
    public sealed class AttributeGrainBindingsProvider : IGrainPropertiesProvider
    {
        /// <summary>
        /// A hopefully unique name to describe bindings added by this provider.
        /// Binding names are meaningless and are only used to group properties for a given binding together.
        /// </summary>
        private const string BindingPrefix = WellKnownGrainTypeProperties.BindingPrefix + ".attr-";
        private readonly IServiceProvider serviceProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="AttributeGrainBindingsProvider"/> class.
        /// </summary>
        /// <param name="serviceProvider">
        /// The service provider.
        /// </param>
        public AttributeGrainBindingsProvider(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public void Populate(Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            var bindingIndex = 1;
            foreach (var attr in grainClass.GetCustomAttributes(inherit: true))
            {
                if (!(attr is IGrainBindingsProviderAttribute providerAttribute))
                {
                    continue;
                }

                foreach (var binding in providerAttribute.GetBindings(this.serviceProvider, grainClass, grainType))
                {
                    foreach (var pair in binding)
                    {
                        properties[BindingPrefix + bindingIndex.ToString(CultureInfo.InvariantCulture) + '.' + pair.Key] = pair.Value; 
                    }

                    ++bindingIndex;
                }
            }
        }
    }
}