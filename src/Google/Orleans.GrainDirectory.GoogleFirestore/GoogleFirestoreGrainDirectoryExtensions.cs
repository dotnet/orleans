using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.GrainDirectory.GoogleFirestore;
using Orleans.Runtime.Hosting;

namespace Orleans.Hosting;

public static class GoogleFirestoreGrainDirectoryExtensions
{
    internal static IServiceCollection AddGoogleFirestoreGrainDirectory(
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
                    ActivatorUtilities.CreateInstance<GoogleFirestoreGrainDirectory>(sp,
                        sp.GetOptionsByName<FirestoreOptions>(name)));

        return services;
    }

    public static ISiloBuilder UseGoogleFirestoreGrainDirectoryAsDefault(
        this ISiloBuilder builder,
        Action<FirestoreOptions> configureOptions)
    {
        return builder.UseGoogleFirestoreGrainDirectoryAsDefault(ob => ob.Configure(configureOptions));
    }

    public static ISiloBuilder UseGoogleFirestoreGrainDirectoryAsDefault(
        this ISiloBuilder builder,
        Action<OptionsBuilder<FirestoreOptions>> configureOptions)
    {
        return builder.ConfigureServices(services =>
            services.AddGoogleFirestoreGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, configureOptions));
    }

    public static ISiloBuilder AddGoogleFirestoreGrainDirectory(
        this ISiloBuilder builder,
        string name,
        Action<FirestoreOptions> configureOptions)
    {
        return builder.AddGoogleFirestoreGrainDirectory(name, ob => ob.Configure(configureOptions));
    }

    public static ISiloBuilder AddGoogleFirestoreGrainDirectory(
        this ISiloBuilder builder,
        string name,
        Action<OptionsBuilder<FirestoreOptions>> configureOptions)
    {
        return builder.ConfigureServices(services => services.AddGoogleFirestoreGrainDirectory(name, configureOptions));
    }
}