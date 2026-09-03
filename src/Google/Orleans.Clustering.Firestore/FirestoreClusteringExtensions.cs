using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging;
using Orleans.Configuration;
using Orleans.Clustering.Firestore;

namespace Orleans.Hosting;

/// <summary>
/// Provides extension methods for configuring Google Firestore clustering.
/// </summary>
public static class FirestoreClusteringExtensions
{
    /// <summary>
    /// Configures the silo to use Google Firestore for clustering.
    /// </summary>
    /// <param name="builder">
    /// The silo builder.
    /// </param>
    /// <param name="configureOptions">
    /// The configuration delegate.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>.
    /// </returns>
    public static ISiloBuilder UseFirestoreClustering(
        this ISiloBuilder builder,
        Action<FirestoreOptions>? configureOptions)
    {
        return builder.UseFirestoreClustering(options =>
        {
            if (configureOptions is not null)
            {
                options.Configure(configureOptions);
            }
        });
    }

    /// <summary>
    /// Configures the silo to use Google Firestore for clustering.
    /// </summary>
    /// <param name="builder">
    /// The silo builder.
    /// </param>
    /// <param name="configureOptions">
    /// The configuration delegate.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>.
    /// </returns>
    public static ISiloBuilder UseFirestoreClustering(
        this ISiloBuilder builder,
        Action<OptionsBuilder<FirestoreOptions>> configureOptions)
    {
        return builder.ConfigureServices(
            services =>
            {
                configureOptions?.Invoke(services.AddOptions<FirestoreOptions>());
                services.AddTransient<IConfigurationValidator>(sp =>
                    new FirestoreOptionsValidator<FirestoreOptions>(
                        sp.GetRequiredService<IOptionsMonitor<FirestoreOptions>>()
                            .Get(Options.DefaultName)));
                services.AddSingleton<IMembershipTable, FirestoreMembershipTable>()
                    .ConfigureFormatter<FirestoreOptions>();
            });
    }

    /// <summary>
    /// Configures the client to use Google Firestore for clustering.
    /// </summary>
    /// <param name="builder">
    /// The client builder.
    /// </param>
    /// <param name="configureOptions">
    /// The configuration delegate.
    /// </param>
    /// <returns>
    /// The provided <see cref="IClientBuilder"/>.
    /// </returns>
    public static IClientBuilder UseFirestoreClustering(
        this IClientBuilder builder,
        Action<FirestoreOptions>? configureOptions)
    {
        return builder.UseFirestoreClustering(options =>
        {
            if (configureOptions is not null)
            {
                options.Configure(configureOptions);
            }
        });
    }

    /// <summary>
    /// Configures the client to use Google Firestore for clustering.
    /// </summary>
    /// <param name="builder">
    /// The client builder.
    /// </param>
    /// <param name="configureOptions">
    /// The configuration delegate.
    /// </param>
    /// <returns>
    /// The provided <see cref="IClientBuilder"/>.
    /// </returns>
    public static IClientBuilder UseFirestoreClustering(
        this IClientBuilder builder,
        Action<OptionsBuilder<FirestoreOptions>> configureOptions)
    {
        return builder.ConfigureServices(
            services =>
            {
                configureOptions?.Invoke(services.AddOptions<FirestoreOptions>());
                services.AddTransient<IConfigurationValidator>(sp =>
                    new FirestoreOptionsValidator<FirestoreOptions>(
                        sp.GetRequiredService<IOptionsMonitor<FirestoreOptions>>().Get(Options.DefaultName)));
                services.AddSingleton<IGatewayListProvider, FirestoreGatewayListProvider>()
                    .ConfigureFormatter<FirestoreOptions>();
            });
    }
}