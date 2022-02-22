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
            IEnumerable<IGrainPropertiesProvider> grainPropertiesProviders,
            IEnumerable<IGrainInterfacePropertiesProvider> grainInterfacePropertiesProviders,
            IOptions<GrainTypeOptions> grainTypeOptions,
            GrainTypeResolver grainTypeResolver,
            GrainInterfaceTypeResolver interfaceTypeResolver)
        {
            var grainProperties = CreateGrainManifest(grainPropertiesProviders, grainTypeOptions, grainTypeResolver);
            var interfaces = CreateInterfaceManifest(grainInterfacePropertiesProviders, grainTypeOptions, interfaceTypeResolver);
            this.ClientManifest = new GrainManifest(grainProperties, interfaces);
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
                var properties = new Dictionary<string, string>();
                foreach (var provider in propertyProviders)
                {
                    provider.Populate(grainInterface, interfaceId, properties);
                }

                var result = new GrainInterfaceProperties(properties.ToImmutableDictionary());
                if (builder.ContainsKey(interfaceId))
                {
                    throw new InvalidOperationException($"An entry with the key {interfaceId} is already present."
                        + $"\nExisting: {builder[interfaceId].ToDetailedString()}\nTrying to add: {result.ToDetailedString()}"
                        + "\nConsider using the [GrainInterfaceType(\"name\")] attribute to give these interfaces unique names.");
                }

                builder.Add(interfaceId, result);
            }

            return builder.ToImmutable();
        }

        private static ImmutableDictionary<GrainType, GrainProperties> CreateGrainManifest(
            IEnumerable<IGrainPropertiesProvider> grainMetadataProviders,
            IOptions<GrainTypeOptions> grainTypeOptions,
            GrainTypeResolver grainTypeProvider)
        {
            var propertiesMap = ImmutableDictionary.CreateBuilder<GrainType, GrainProperties>();
            foreach (var grainClass in grainTypeOptions.Value.Classes)
            {
                var grainType = grainTypeProvider.GetGrainType((Type)grainClass);
                var properties = new Dictionary<string, string>();
                foreach (var provider in grainMetadataProviders)
                {
                    provider.Populate((Type)grainClass, grainType, properties);
                }

                var result = new GrainProperties(properties.ToImmutableDictionary());
                if (propertiesMap.ContainsKey(grainType))
                {
                    throw new InvalidOperationException($"An entry with the key {grainType} is already present."
                        + $"\nExisting: {propertiesMap[grainType].ToDetailedString()}\nTrying to add: {result.ToDetailedString()}"
                        + "\nConsider using the [GrainType(\"name\")] attribute to give these classes unique names.");
                }

                propertiesMap.Add(grainType, result);
            }

            return propertiesMap.ToImmutable();
        }
    }
}
