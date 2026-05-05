using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration.Internal;
using Orleans.DurableJobs;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime.Hosting;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Orleans.Runtime;

namespace Orleans.Hosting;

public static class SiloBuilderReminderExtensions
{
    public static ISiloBuilder AddAdvancedReminders(this ISiloBuilder builder)
        => builder.ConfigureServices(static services => AddAdvancedReminders(services));

    public static ISiloBuilder AddAdvancedReminders(this ISiloBuilder builder, Action<ReminderOptions> configureOptions)
        => builder.ConfigureServices(services => AddAdvancedReminders(services, configureOptions));

    public static void AddAdvancedReminders(this IServiceCollection services)
    {
        if (services.Any(service => service.ServiceType == typeof(AdvancedReminderService)))
        {
            return;
        }

        services.AddDurableJobs();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IConfigurationValidator, ReminderOptionsValidator>();
        services.AddSingleton<IConfigurationValidator, AdvancedReminderJobBackendValidator>();
        services.AddSingleton<AdvancedReminderService>();
        services.AddFromExisting<Orleans.AdvancedReminders.IReminderService, AdvancedReminderService>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, AdvancedReminderService>();
        services.AddSingleton<Orleans.AdvancedReminders.Timers.IReminderRegistry, ReminderRegistry>();
        services.AddSingleton<IConfigureGrainContextProvider, RegisterReminderActivationConfiguratorProvider>();
    }

    public static void AddAdvancedReminders(this IServiceCollection services, Action<ReminderOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);
        AddAdvancedReminders(services);
        services.Configure(configureOptions);
    }
}
