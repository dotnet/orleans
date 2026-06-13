using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Metadata;

namespace Orleans.Runtime
{
    /// <summary>
    /// Creates a manifest of the locally available grain interface types.
    /// </summary>
    internal class ClientManifestProvider
    {
        public ClientManifestProvider(
            IEnumerable<IGrainInterfacePropertiesProvider> grainInterfacePropertiesProviders,
            IOptions<GrainTypeOptions> grainTypeOptions,
            GrainInterfaceTypeResolver interfaceTypeResolver)
        {
            var interfaces = CreateInterfaceManifest(grainInterfacePropertiesProviders, grainTypeOptions, interfaceTypeResolver);
            this.ClientManifest = new GrainManifest(ImmutableDictionary<GrainType, GrainProperties>.Empty, interfaces);
        }

        /// <summary>
        /// Gets the client manifest.
        /// </summary>
        public GrainManifest ClientManifest { get; }

        private static ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties> CreateInterfaceManifest(
            IEnumerable<IGrainInterfacePropertiesProvider> propertyProviders,
            IOptions<GrainTypeOptions> grainTypeOptions,
            GrainInterfaceTypeResolver interfaceTypeResolver)
        {
            var builder = ImmutableDictionary.CreateBuilder<GrainInterfaceType, GrainInterfaceProperties>();
            foreach (var grainInterface in grainTypeOptions.Value.Interfaces)
            {
                var interfaceId = interfaceTypeResolver.GetGrainInterfaceType(grainInterface);
                var properties = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var provider in propertyProviders)
                {
                    provider.Populate(grainInterface, interfaceId, properties);
                }

                var result = new GrainInterfaceProperties(ToImmutablePropertyDictionary(properties));
                if (builder.TryGetValue(interfaceId, out var grainInterfaceProperty))
                {
                    throw new InvalidOperationException($"An entry with the key {interfaceId} is already present."
                        + $"\nExisting: {grainInterfaceProperty.ToDetailedString()}\nTrying to add: {result.ToDetailedString()}"
                        + "\nConsider using the [GrainInterfaceType(\"name\")] attribute to give these interfaces unique names.");
                }

                builder.Add(interfaceId, result);
            }

            return builder.ToImmutable();
        }

        private static ImmutableDictionary<string, string> ToImmutablePropertyDictionary(Dictionary<string, string> properties)
        {
            var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal, StringComparer.Ordinal);
            foreach (var property in properties)
            {
                builder.Add(property.Key, property.Value);
            }

            return builder.ToImmutable();
        }
    }
}
