using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Messaging.Configuration;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Journaling.Json;

namespace Orleans.Hosting;

/// <summary>
/// Extensions for configuring messaging.
/// </summary>
public static class MessagingExtensions
{
    /// <summary>
    /// Adds inbox and outbox messaging support to the silo.
    /// </summary>
    /// <remarks>
    /// Grains implementing <see cref="IMessagingGrain"/> or deriving from <see cref="DurableGrain"/>
    /// bind their scoped messaging services during activation setup, before journal recovery.
    /// </remarks>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The optional action used to configure inbox and outbox behavior.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddMessaging(this ISiloBuilder builder, Action<DurableMessagingOptions>? configureOptions = null)
    {
        builder.AddDurableJobs();
        return builder.ConfigureServices(services => services.AddMessaging(configureOptions));
    }

    /// <summary>
    /// Adds inbox and outbox messaging services.
    /// </summary>
    /// <remarks>
    /// Grains implementing <see cref="IMessagingGrain"/> or deriving from <see cref="DurableGrain"/>
    /// bind their scoped messaging services during activation setup, before journal recovery.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">The optional action used to configure inbox and outbox behavior.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddMessaging(this IServiceCollection services, Action<DurableMessagingOptions>? configureOptions = null)
    {
        services.AddDurableJobs();
        services.TryAddSingleton(TimeProvider.System);
        services.Configure<JsonJournalOptions>(options =>
        {
            options.AddTypeInfoResolver(InboxJsonContext.Default);
            options.AddTypeInfoResolver(OutboxJsonContext.Default);
        });

        var optionsBuilder = services.AddOptions<DurableMessagingOptions>();
        if (configureOptions is not null)
        {
            optionsBuilder.Configure(configureOptions);
        }

        optionsBuilder.Validate(
            options =>
            {
                try
                {
                    options.Validate();
                    return true;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            },
            "DurableMessagingOptions validation failed.");

        services.ConfigureNamedOptionForLogging<DurableMessagingOptions>(Options.DefaultName);
        services.TryAddSingleton<MessagingInstruments>();
        services.TryAddScoped<MessagingPump>();
        services.TryAddScoped<Inbox>();
        services.Replace(ServiceDescriptor.Scoped<IInbox>(static sp => sp.GetRequiredService<Inbox>()));
        services.TryAddScoped<Outbox>();
        services.Replace(ServiceDescriptor.Scoped<IOutbox>(static sp => sp.GetRequiredService<Outbox>()));
        services.TryAddScoped<IMessagingDiagnostics, MessagingDiagnostics>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<Orleans.Runtime.IConfigureGrainTypeComponents, MessagingGrainTypeConfigurator>());
        return services;
    }
}
