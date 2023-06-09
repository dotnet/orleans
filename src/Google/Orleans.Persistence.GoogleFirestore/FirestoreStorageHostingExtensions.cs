using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Storage;
using Orleans.Runtime;
using Orleans.Providers;
using Orleans.Configuration;
using Orleans.Persistence.GoogleFirestore;


namespace Orleans.Hosting;

/// <summary>
/// <see cref="IServiceCollection"/> and <see cref="ISiloBuilder"/> extensions.
/// </summary>
public static class FirestoreStorageHostingExtensions
{
    /// <summary>
    /// Configure silo to use Google Firestore storage as the default grain storage.
    /// </summary>
    public static IServiceCollection AddGoogleFirestoreGrainStorageAsDefault(this IServiceCollection services, Action<FirestoreStateStorageOptions> configureOptions)
    {
        return services.AddGoogleFirestoreGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, ob => ob.Configure(configureOptions));
    }

    /// <summary>
    /// Configure silo to use Google Firestore storage for grain storage.
    /// </summary>
    public static IServiceCollection AddGoogleFirestoreGrainStorage(this IServiceCollection services, string name, Action<FirestoreStateStorageOptions> configureOptions)
    {
        return services.AddGoogleFirestoreGrainStorage(name, ob => ob.Configure(configureOptions));
    }

    /// <summary>
    /// Configure silo to use Google Firestore storage as the default grain storage.
    /// </summary>
    public static IServiceCollection AddGoogleFirestoreGrainStorageAsDefault(this IServiceCollection services, Action<OptionsBuilder<FirestoreStateStorageOptions>>? configureOptions = null)
    {
        return services.AddGoogleFirestoreGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use Google Firestore storage for grain storage.
    /// </summary>
    public static IServiceCollection AddGoogleFirestoreGrainStorage(this IServiceCollection services, string name,
        Action<OptionsBuilder<FirestoreStateStorageOptions>>? configureOptions = null)
    {
        configureOptions?.Invoke(services.AddOptions<FirestoreStateStorageOptions>(name));
        services.AddTransient<IConfigurationValidator>(sp => new FirestoreStateStorageOptionsValidator(sp.GetRequiredService<IOptionsMonitor<FirestoreStateStorageOptions>>().Get(name), name));
        services.ConfigureNamedOptionForLogging<FirestoreStateStorageOptions>(name);

        if (string.Equals(name, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, StringComparison.Ordinal))
        {
            services.TryAddSingleton(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        }

        return services.AddSingletonNamedService(name, GoogleFirestoreStorageFactory.Create)
            .AddSingletonNamedService(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
    }

    /// <summary>
    /// Configure silo to use AWS DynamoDB storage as the default grain storage.
    /// </summary>
    public static ISiloBuilder AddGoogleFirestoreGrainStorageAsDefault(this ISiloBuilder builder, Action<FirestoreStateStorageOptions> configureOptions)
    {
        return builder.AddGoogleFirestoreGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use AWS DynamoDB storage for grain storage.
    /// </summary>
    public static ISiloBuilder AddGoogleFirestoreGrainStorage(this ISiloBuilder builder, string name, Action<FirestoreStateStorageOptions> configureOptions)
    {
        return builder.ConfigureServices(services => services.AddGoogleFirestoreGrainStorage(name, configureOptions));
    }

    /// <summary>
    /// Configure silo to use AWS DynamoDB storage as the default grain storage.
    /// </summary>
    public static ISiloBuilder AddGoogleFirestoreGrainStorageAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<FirestoreStateStorageOptions>>? configureOptions = null)
    {
        return builder.AddGoogleFirestoreGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use AWS DynamoDB storage for grain storage.
    /// </summary>
    public static ISiloBuilder AddGoogleFirestoreGrainStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<FirestoreStateStorageOptions>>? configureOptions = null)
    {
        return builder.ConfigureServices(services => services.AddGoogleFirestoreGrainStorage(name, configureOptions));
    }
}
