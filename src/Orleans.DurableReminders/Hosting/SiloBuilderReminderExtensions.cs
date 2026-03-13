using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Orleans.DurableJobs;
using Orleans.DurableReminders;
using Orleans.DurableReminders.Runtime.ReminderService;
using Orleans.Runtime;

namespace Orleans.Hosting;

public static class SiloBuilderReminderExtensions
{
    public static ISiloBuilder AddDurableReminders(this ISiloBuilder builder)
        => builder.ConfigureServices(static services => AddDurableReminders(services));

    public static ISiloBuilder AddDurableReminders(this ISiloBuilder builder, Action<DurableReminders.ReminderOptions> configureOptions)
        => builder.ConfigureServices(services => AddDurableReminders(services, configureOptions));

    public static void AddDurableReminders(this IServiceCollection services)
    {
        if (services.Any(service => service.ServiceType == typeof(DurableReminderService)))
        {
            return;
        }

        services.AddDurableJobs();
        services.AddSingleton<IConfigurationValidator, DurableReminders.ReminderOptionsValidator>();
        services.AddSingleton<DurableReminderService>();
        services.AddFromExisting<Orleans.DurableReminders.IReminderService, DurableReminderService>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, DurableReminderService>();
        services.AddSingleton<Orleans.DurableReminders.Timers.IReminderRegistry, ReminderRegistry>();
        services.AddSingleton<IConfigureGrainContextProvider, RegisterReminderActivationConfiguratorProvider>();
    }

    public static void AddDurableReminders(this IServiceCollection services, Action<DurableReminders.ReminderOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);
        AddDurableReminders(services);
        services.Configure(configureOptions);
    }
}
