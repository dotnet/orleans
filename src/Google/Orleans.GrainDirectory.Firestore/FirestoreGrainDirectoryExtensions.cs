using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.GrainDirectory.Firestore;
using Orleans.Runtime.Hosting;

namespace Orleans.Hosting;

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

    public static ISiloBuilder UseFirestoreGrainDirectoryAsDefault(
        this ISiloBuilder builder,
        Action<FirestoreOptions> configureOptions)
    {
        return builder.UseFirestoreGrainDirectoryAsDefault(ob => ob.Configure(configureOptions));
    }

    public static ISiloBuilder UseFirestoreGrainDirectoryAsDefault(
        this ISiloBuilder builder,
        Action<OptionsBuilder<FirestoreOptions>> configureOptions)
    {
        return builder.ConfigureServices(services =>
            services.AddFirestoreGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, configureOptions));
    }

    public static ISiloBuilder AddFirestoreGrainDirectory(
        this ISiloBuilder builder,
        string name,
        Action<FirestoreOptions> configureOptions)
    {
        return builder.AddFirestoreGrainDirectory(name, ob => ob.Configure(configureOptions));
    }

    public static ISiloBuilder AddFirestoreGrainDirectory(
        this ISiloBuilder builder,
        string name,
        Action<OptionsBuilder<FirestoreOptions>> configureOptions)
    {
        return builder.ConfigureServices(services => services.AddFirestoreGrainDirectory(name, configureOptions));
    }
}