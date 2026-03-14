using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Orleans.Runtime;

namespace Orleans.Hosting;

/// <summary>
/// Extensions to <see cref="ISiloBuilder"/> for configuring the in-memory durable reminder provider.
/// </summary>
public static class SiloBuilderReminderMemoryExtensions
{
    /// <summary>
    /// Configures durable reminder storage using an in-memory, non-persistent store.
    /// </summary>
    public static ISiloBuilder UseInMemoryAdvancedReminderService(this ISiloBuilder builder)
    {
        builder.AddAdvancedReminders();
        builder.UseInMemoryDurableJobs();
        builder.ConfigureServices(static services =>
        {
            services.AddSingleton<InMemoryReminderTable>();
            services.AddFromExisting<Orleans.AdvancedReminders.IReminderTable, InMemoryReminderTable>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, InMemoryReminderTable>();
        });
        return builder;
    }
}
