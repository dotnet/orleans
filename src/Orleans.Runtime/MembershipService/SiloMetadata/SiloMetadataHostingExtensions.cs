using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Orleans.Hosting;
using Orleans.Placement;
using Orleans.Runtime.Placement.Filtering;

namespace Orleans.Runtime.MembershipService.SiloMetadata;

/// <summary>
/// Extensions for configuring silo metadata.
/// </summary>
public static class SiloMetadataHostingExtensions
{
    /// <summary>
    /// Configures silo metadata from the builder configuration.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <remarks>
    /// Reads the <c>Orleans:Metadata</c> configuration section.
    /// Key-value pairs can be supplied using environment variables such as:
    /// ORLEANS__METADATA__key1=value1
    /// </remarks>
    /// <returns>The provided silo builder.</returns>
    public static ISiloBuilder UseSiloMetadata(this ISiloBuilder builder) => builder.UseSiloMetadata(builder.Configuration);

    /// <summary>
    /// Configures silo metadata from the provided configuration.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configuration">The configuration containing the silo metadata.</param>
    /// <remarks>
    /// Reads the <c>Orleans:Metadata</c> configuration section.
    /// Key-value pairs can be supplied using environment variables such as:
    /// ORLEANS__METADATA__key1=value1
    /// </remarks>
    /// <returns>The provided silo builder.</returns>
    public static ISiloBuilder UseSiloMetadata(this ISiloBuilder builder, IConfiguration configuration)
    {

        var metadataConfigSection = configuration.GetSection("Orleans").GetSection("Metadata");

        return builder.UseSiloMetadata(metadataConfigSection);
    }

    /// <summary>
    /// Configures silo metadata from the provided configuration section.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configurationSection">The configuration section containing the silo metadata.</param>
    /// <remarks>
    /// Binds the provided section as a <see cref="Dictionary{TKey,TValue}"/>.
    /// Key-value pairs can be supplied using environment variables such as:
    /// ORLEANS__METADATA__key1=value1
    /// </remarks>
    /// <returns>The provided silo builder.</returns>
    public static ISiloBuilder UseSiloMetadata(this ISiloBuilder builder, IConfigurationSection configurationSection)
    {
        var dictionary = configurationSection.Get<Dictionary<string, string>>();

        return builder.UseSiloMetadata(dictionary ?? []);
    }

    /// <summary>
    /// Configures the metadata published by the local silo.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="metadata">The metadata to publish for the local silo.</param>
    /// <returns>The provided silo builder.</returns>
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