using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Orleans.Hosting;
using Orleans.Placement;
using Orleans.Runtime.Placement.Filtering;

#nullable enable
namespace Orleans.Runtime.MembershipService.SiloMetadata;

public static class SiloMetadataHostingExtensions
{
    /// <summary>
    /// Configure silo metadata from the builder configuration.
    /// </summary>
    /// <param name="builder">Silo builder</param>
    /// <remarks>
    /// Get the ORLEANS__METADATA section from config
    /// Key/value pairs in configuration as a <see cref="Dictionary{TKey,TValue}"/> will look like this as environment variables:
    /// ORLEANS__METADATA__key1=value1
    /// </remarks>
    /// <returns></returns>
    public static ISiloBuilder UseSiloMetadata(this ISiloBuilder builder) => builder.UseSiloMetadata(builder.Configuration);

    /// <summary>
    /// Configure silo metadata from configuration.
    /// </summary>
    /// <param name="builder">Silo builder</param>
    /// <param name="configuration">Configuration to pull from</param>
    /// <remarks>
    /// Get the ORLEANS__METADATA section from config
    /// Key/value pairs in configuration as a <see cref="Dictionary{TKey,TValue}"/> will look like this as environment variables:
    /// ORLEANS__METADATA__key1=value1
    /// </remarks>
    /// <returns></returns>
    public static ISiloBuilder UseSiloMetadata(this ISiloBuilder builder, IConfiguration configuration)
    {

        var metadataConfigSection = configuration.GetSection("Orleans").GetSection("Metadata");

        return builder.UseSiloMetadata(metadataConfigSection);
    }

    /// <summary>
    /// Configure silo metadata from configuration section.
    /// </summary>
    /// <param name="builder">Silo builder</param>
    /// <param name="configurationSection">Configuration section to pull from</param>
    /// <remarks>
    /// Get the ORLEANS__METADATA section from config section
    /// Key/value pairs in configuration as a <see cref="Dictionary{TKey,TValue}"/> will look like this as environment variables:
    /// ORLEANS__METADATA__key1=value1
    /// </remarks>
    /// <returns></returns>
    public static ISiloBuilder UseSiloMetadata(this ISiloBuilder builder, IConfigurationSection configurationSection)
    {
        var dictionary = configurationSection.Get<Dictionary<string, string>>();

        return builder.UseSiloMetadata(dictionary ?? []);
    }

    /// <summary>
    /// Configure silo metadata from configuration section.
    /// </summary>
    /// <param name="builder">Silo builder</param>
    /// <param name="metadata">Metadata to add</param>
    /// <returns></returns>
    public static ISiloBuilder UseSiloMetadata(this ISiloBuilder builder, Dictionary<string, string> metadata)
    {
        builder.ConfigureServices(services =>
        {
            services
                .AddOptionsWithValidateOnStart<SiloMetadata>()
                .Configure(m => m.AddMetadata(metadata));

            services.AddSingleton<SiloMetadataSystemTarget>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, SiloMetadataSystemTarget>();
            services.AddSingleton<SiloMetadataCache>();
            services.AddFromExisting<ISiloMetadataCache, SiloMetadataCache>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, SiloMetadataCache>();
            services.AddSingleton<ISiloMetadataClient, SiloMetadataClient>();

            // Placement filters
            services.AddPlacementFilter<PreferredMatchSiloMetadataPlacementFilterStrategy, PreferredMatchSiloMetadataPlacementFilterDirector>(ServiceLifetime.Transient);
            services.AddPlacementFilter<RequiredMatchSiloMetadataPlacementFilterStrategy, RequiredMatchSiloMetadataPlacementFilterDirector>(ServiceLifetime.Transient);
        });
        return builder;
    }
}