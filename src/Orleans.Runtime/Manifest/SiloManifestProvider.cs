using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Metadata
{
    /// <summary>
    /// Creates a <see cref="SiloManifest"/> for this silo.
    /// </summary>
    internal class SiloManifestProvider
    {
        private readonly IEnumerable<IGrainPropertiesProvider> _grainPropertiesProviders;
        private readonly IEnumerable<IGrainInterfacePropertiesProvider> _grainInterfacePropertiesProviders;
        private readonly IOptions<GrainTypeOptions> _grainTypeOptions;
        private readonly GrainTypeResolver _typeProvider;
        private readonly GrainInterfaceTypeResolver _interfaceIdProvider;

        public SiloManifestProvider(
            IEnumerable<IGrainPropertiesProvider> grainPropertiesProviders,
            IEnumerable<IGrainInterfacePropertiesProvider> grainInterfacePropertiesProviders,
            IOptions<GrainTypeOptions> grainTypeOptions,
            GrainTypeResolver typeProvider,
            GrainInterfaceTypeResolver interfaceIdProvider,
            TypeConverter typeConverter)
        {
            _grainPropertiesProviders = grainPropertiesProviders;
            _grainInterfacePropertiesProviders = grainInterfacePropertiesProviders;
            _grainTypeOptions = grainTypeOptions;
            _typeProvider = typeProvider;
            _interfaceIdProvider = interfaceIdProvider;
            var (grainProperties, grainTypes) = CreateGrainManifest(grainPropertiesProviders, grainTypeOptions, typeProvider);
            var interfaces = CreateInterfaceManifest(grainInterfacePropertiesProviders, grainTypeOptions, interfaceIdProvider);
            this.SiloManifest = new GrainManifest(grainProperties, interfaces);
            this.GrainTypeMap = new GrainClassMap(typeConverter, grainTypes);
        }

        public GrainManifest SiloManifest { get; private set; }

        public GrainClassMap GrainTypeMap { get; }

        /// <summary>
        /// Rebuilds the silo manifest and grain class map after a hot reload metadata update. Throws if the
        /// rebuilt manifest is invalid (e.g. duplicate grain type names); callers must leave the previous
        /// manifest in place in that case.
        /// </summary>
        public void OnManifestUpdated()
        {
            var (grainProperties, grainTypes) = CreateGrainManifest(_grainPropertiesProviders, _grainTypeOptions, _typeProvider);
            var interfaces = CreateInterfaceManifest(_grainInterfacePropertiesProviders, _grainTypeOptions, _interfaceIdProvider);
            SiloManifest = new GrainManifest(grainProperties, interfaces);
            GrainTypeMap.OnManifestUpdated(grainTypes);
        }

        private static ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties> CreateInterfaceManifest(
            IEnumerable<IGrainInterfacePropertiesProvider> propertyProviders,
            IOptions<GrainTypeOptions> grainTypeOptions,
            GrainInterfaceTypeResolver grainInterfaceIdProvider)
        {
            var builder = ImmutableDictionary.CreateBuilder<GrainInterfaceType, GrainInterfaceProperties>();
            foreach (var grainInterface in grainTypeOptions.Value.Interfaces)
            {
                var interfaceId = grainInterfaceIdProvider.GetGrainInterfaceType(grainInterface);
                var properties = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var provider in propertyProviders)
                {
                    provider.Populate(grainInterface, interfaceId, properties);
                }

                var result = new GrainInterfaceProperties(ToImmutablePropertyDictionary(properties));
                if (builder.TryGetValue(interfaceId, out var graintInterfaceProperty))
                {
                    throw new InvalidOperationException($"An entry with the key {interfaceId} is already present."
                        + $"\nExisting: {graintInterfaceProperty.ToDetailedString()}\nTrying to add: {result.ToDetailedString()}"
                        + "\nConsider using the [GrainInterfaceType(\"name\")] attribute to give these interfaces unique names.");
                }

                builder.Add(interfaceId, result);
            }

            return builder.ToImmutable();
        }

        private static (ImmutableDictionary<GrainType, GrainProperties>, ImmutableDictionary<GrainType, Type>) CreateGrainManifest(
            IEnumerable<IGrainPropertiesProvider> grainMetadataProviders,
            IOptions<GrainTypeOptions> grainTypeOptions,
            GrainTypeResolver grainTypeProvider)
        {
            var propertiesMap = ImmutableDictionary.CreateBuilder<GrainType, GrainProperties>();
            var typeMap = ImmutableDictionary.CreateBuilder<GrainType, Type>();
            foreach (var grainClass in grainTypeOptions.Value.Classes)
            {
                var grainType = grainTypeProvider.GetGrainType(grainClass);
                var properties = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var provider in grainMetadataProviders)
                {
                    provider.Populate(grainClass, grainType, properties);
                }

                var result = new GrainProperties(ToImmutablePropertyDictionary(properties));
                if (propertiesMap.TryGetValue(grainType, out var grainProperty))
                {
                    throw new InvalidOperationException($"An entry with the key {grainType} is already present."
                        + $"\nExisting: {grainProperty.ToDetailedString()}\nTrying to add: {result.ToDetailedString()}"
                        + "\nConsider using the [GrainType(\"name\")] attribute to give these classes unique names.");
                }

                propertiesMap.Add(grainType, result);
                typeMap.Add(grainType, grainClass);
            }

            return (propertiesMap.ToImmutable(), typeMap.ToImmutable());
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
