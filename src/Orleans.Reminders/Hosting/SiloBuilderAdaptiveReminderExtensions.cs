using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Configuration.Internal;
using Orleans.Runtime.ReminderService;

namespace Orleans.Hosting;

/// <summary>
/// Extensions for enabling the adaptive Orleans reminder service.
/// </summary>
public static class SiloBuilderAdaptiveReminderExtensions
{
    /// <summary>
    /// Enables the adaptive reminder service while preserving backward compatibility with existing reminder storage providers.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <returns>The builder.</returns>
    public static ISiloBuilder AddAdaptiveReminderService(this ISiloBuilder builder)
    {
        builder.ConfigureServices(services => AddAdaptiveReminderService(services));
        return builder;
    }

    /// <summary>
    /// Enables the adaptive reminder service and applies reminder options configuration.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The reminder options configuration callback.</param>
    /// <returns>The builder.</returns>
    public static ISiloBuilder AddAdaptiveReminderService(this ISiloBuilder builder, Action<ReminderOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        builder.ConfigureServices(services => AddAdaptiveReminderService(services, configureOptions));
        return builder;
    }

    /// <summary>
    /// Enables the adaptive reminder service while preserving backward compatibility with existing reminder storage providers.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static void AddAdaptiveReminderService(this IServiceCollection services)
    {
        if (services.Any(service => service.ServiceType == typeof(AdaptiveReminderService)))
        {
            return;
        }

        if (services.All(service => service.ServiceType != typeof(LocalReminderService)))
        {
            services.AddReminders();
        }

        if (services.All(service => service.ServiceType != typeof(AdaptiveReminderServiceRegistrationMarker)))
        {
            services.AddSingleton<AdaptiveReminderServiceRegistrationMarker>();
        }

        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(IReminderService))
            {
                services.RemoveAt(i);
            }
        }

        services.AddSingleton<AdaptiveReminderService>();
        services.AddFromExisting<IReminderService, AdaptiveReminderService>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, AdaptiveReminderService>();
    }

    /// <summary>
    /// Enables the adaptive reminder service and applies reminder options configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">The reminder options configuration callback.</param>
    public static void AddAdaptiveReminderService(this IServiceCollection services, Action<ReminderOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        AddAdaptiveReminderService(services);
        services.Configure(configureOptions);
    }
}
