using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.GrainDirectory.Firestore;
using Orleans.Runtime.Hosting;

namespace Orleans.Hosting;

/// <summary>
/// Extension methods for configuring a Google Cloud Firestore grain directory.
/// </summary>
public static class FirestoreGrainDirectoryExtensions
{
    internal static IServiceCollection AddFirestoreGrainDirectory(
        this IServiceCollection services,
        string name,
        Action<OptionsBuilder<FirestoreOptions>> configureOptions)
    {
        configureOptions.Invoke(services.AddOptions<FirestoreOptions>(name));
        services
            .AddTransient<IConfigurationValidator>(sp =>
                new FirestoreOptionsValidator<FirestoreOptions>(
                    sp.GetRequiredService<IOptionsMonitor<FirestoreOptions>>().Get(name)))
            .ConfigureNamedOptionForLogging<FirestoreOptions>(name)
            .AddGrainDirectory(name,
                (sp, name) =>
                    ActivatorUtilities.CreateInstance<FirestoreGrainDirectory>(sp,
                        sp.GetOptionsByName<FirestoreOptions>(name)));

        return services;
    }

    /// <summary>
    /// Configures Google Cloud Firestore as the default grain directory.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder UseFirestoreGrainDirectoryAsDefault(
        this ISiloBuilder builder,
        Action<FirestoreOptions> configureOptions)
    {
        return builder.UseFirestoreGrainDirectoryAsDefault(ob => ob.Configure(configureOptions));
    }

    /// <summary>
    /// Configures Google Cloud Firestore as the default grain directory.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider options builder.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder UseFirestoreGrainDirectoryAsDefault(
        this ISiloBuilder builder,
        Action<OptionsBuilder<FirestoreOptions>> configureOptions)
    {
        return builder.ConfigureServices(services =>
            services.AddFirestoreGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, configureOptions));
    }

    /// <summary>
    /// Adds a named Google Cloud Firestore grain directory.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The name of the grain directory.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddFirestoreGrainDirectory(
        this ISiloBuilder builder,
        string name,
        Action<FirestoreOptions> configureOptions)
    {
        return builder.AddFirestoreGrainDirectory(name, ob => ob.Configure(configureOptions));
    }

    /// <summary>
    /// Adds a named Google Cloud Firestore grain directory.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The name of the grain directory.</param>
    /// <param name="configureOptions">The delegate used to configure the provider options builder.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddFirestoreGrainDirectory(
        this ISiloBuilder builder,
        string name,
        Action<OptionsBuilder<FirestoreOptions>> configureOptions)
    {
        return builder.ConfigureServices(services => services.AddFirestoreGrainDirectory(name, configureOptions));
    }
}