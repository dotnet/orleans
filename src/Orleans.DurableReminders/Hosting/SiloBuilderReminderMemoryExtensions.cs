using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Orleans.DurableReminders;
using Orleans.DurableReminders.Runtime.ReminderService;
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
    public static ISiloBuilder UseInMemoryDurableReminderService(this ISiloBuilder builder)
    {
        builder.AddDurableReminders();
        builder.UseInMemoryDurableJobs();
        builder.ConfigureServices(static services =>
        {
            services.AddSingleton<InMemoryReminderTable>();
            services.AddFromExisting<Orleans.DurableReminders.IReminderTable, InMemoryReminderTable>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, InMemoryReminderTable>();
        });
        return builder;
    }
}
