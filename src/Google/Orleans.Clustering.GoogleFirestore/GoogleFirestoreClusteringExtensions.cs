using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging;
using Orleans.Configuration;
using Orleans.Clustering.GoogleFirestore;

namespace Orleans.Hosting;

public static class GoogleFirestoreClusteringExtensions
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
    public static ISiloBuilder UseGoogleFirestoreClustering(
        this ISiloBuilder builder,
        Action<FirestoreOptions>? configureOptions)
    {
        return builder.ConfigureServices(
            services =>
            {
                if (configureOptions != null)
                {
                    services.Configure(configureOptions);
                }

                services.AddSingleton<IMembershipTable, GoogleFirestoreMembershipTable>()
                    .ConfigureFormatter<FirestoreOptions>();
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
    public static ISiloBuilder UseGoogleFirestoreClustering(
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
                            .Get(Options.DefaultName), Options.DefaultName));
                services.AddSingleton<IMembershipTable, GoogleFirestoreMembershipTable>()
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
    public static IClientBuilder UseGoogleFirestoreClustering(
        this IClientBuilder builder,
        Action<FirestoreOptions>? configureOptions)
    {
        return builder.ConfigureServices(
            services =>
            {
                if (configureOptions != null)
                {
                    services.Configure(configureOptions);
                }

                services.AddSingleton<IGatewayListProvider, GoogleFirestoreGatewayListProvider>()
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
    public static IClientBuilder UseGoogleFirestoreClustering(
        this IClientBuilder builder,
        Action<OptionsBuilder<FirestoreOptions>> configureOptions)
    {
        return builder.ConfigureServices(
            services =>
            {
                configureOptions?.Invoke(services.AddOptions<FirestoreOptions>());
                services.AddTransient<IConfigurationValidator>(sp =>
                    new FirestoreOptionsValidator<FirestoreOptions>(
                        sp.GetRequiredService<IOptionsMonitor<FirestoreOptions>>().Get(Options.DefaultName),
                        Options.DefaultName));
                services.AddSingleton<IGatewayListProvider, GoogleFirestoreGatewayListProvider>()
                    .ConfigureFormatter<FirestoreOptions>();
            });
    }
}