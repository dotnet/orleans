using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Configuration.Internal;
using System.Linq;
using Orleans.Runtime.ReminderService;
using Orleans.Timers;

namespace Orleans.Hosting;

public static class SiloBuilderReminderExtensions
{
    /// <summary>
    /// Adds support for reminders to this silo.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddReminders(this ISiloBuilder builder) => builder.ConfigureServices(services => AddReminders(services));

    /// <summary>
    /// Adds support for reminders to this silo and applies reminder options configuration.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configureOptions">The reminder options configuration callback.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddReminders(this ISiloBuilder builder, Action<ReminderOptions> configureOptions)
        => builder.ConfigureServices(services => AddReminders(services, configureOptions));

    /// <summary>
    /// Add support for reminders to this client.
    /// </summary>
    /// <param name="services">The services.</param>
    public static void AddReminders(this IServiceCollection services)
    {
        if (services.Any(service => service.ServiceType.Equals(typeof(LocalReminderService))))
        {
            return;
        }

        services.AddSingleton<LocalReminderService>();
        services.AddFromExisting<IReminderService, LocalReminderService>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, LocalReminderService>();
        services.AddSingleton<IConfigureGrainContextProvider, RegisterReminderActivationConfiguratorProvider>();
        services.AddSingleton<IReminderRegistry, ReminderRegistry>();
    }

    /// <summary>
    /// Adds support for reminders to this silo and applies reminder options configuration.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="configureOptions">The reminder options configuration callback.</param>
    public static void AddReminders(this IServiceCollection services, Action<ReminderOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        AddReminders(services);
        services.Configure(configureOptions);
    }
}
