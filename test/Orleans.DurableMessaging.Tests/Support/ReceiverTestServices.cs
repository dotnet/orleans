using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Configuration;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Orleans.Timers;

namespace Orleans.DurableMessaging.Tests.Support;

internal static class ReceiverTestServices
{
    public static void Add(IServiceCollection services, Action<DurableInboxOptions> configure)
    {
        services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = "orleans-binary");
        services.AddOptions<DurableInboxOptions>().Configure(configure);
        services.TryAddSingleton<DurableMessagingInstruments>();

        services.TryAddScoped<DurableInboxExtension>(sp =>
        {
            var stateManager = sp.GetRequiredService<IJournaledStateManager>();
            var options = sp.GetRequiredService<IOptions<DurableInboxOptions>>().Value;
            return new DurableInboxExtension(
                sp.GetRequiredService<IGrainContext>(),
                sp.GetRequiredService<IGrainFactory>(),
                sp.GetRequiredService<ITimerRegistry>(),
                stateManager,
                sp.GetRequiredService<SerializerSessionPool>(),
                sp.GetRequiredService<ILogger<DurableInboxExtension>>(),
                sp.GetRequiredService<DurableMessagingInstruments>(),
                sp.GetRequiredService<DurableInbox>(),
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>(DurableMessagingStateNames.Inbox),
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DateTimeOffset>>(DurableMessagingStateNames.InboxProcessed),
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), InboxMessageState>>(DurableMessagingStateNames.InboxMessageState),
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), InboxDeadLetter>>(DurableMessagingStateNames.InboxDeadLetters),
                sp.GetRequiredKeyedService<IDurableValue<string>>(DurableMessagingStateNames.InboxJobId),
                sp.GetRequiredKeyedService<IDurableValue<DurableJob>>(DurableMessagingStateNames.InboxJobHandle),
                sp.GetRequiredKeyedService<IDurableValue<string>>(DurableMessagingStateNames.InboxCompletedJobId),
                sp.GetRequiredKeyedService<IDurableValue<long>>(DurableMessagingStateNames.InboxJobSequence),
                sp.GetRequiredService<IDurableOutbox>(),
                sp.GetRequiredService<ILocalDurableJobManager>(),
                sp.GetRequiredService<IDurableJobHandlerRegistry>(),
                sp.GetRequiredService<DurableMessagingPumpResults>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredKeyedService<TimeProvider>(DurableJobTimeProviderNames.DurableJobs),
                options);
        });

        services.TryAddKeyedScoped<IGrainExtension>(
            typeof(IDurableInboxExtension),
            (sp, _) => sp.GetRequiredService<DurableInboxExtension>());
        services.TryAddScoped<DurableInbox>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DurableInboxOptions>>().Value;
            _ = sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), InboxMessageState>>(DurableMessagingStateNames.InboxMessageState);
            _ = sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), InboxDeadLetter>>(DurableMessagingStateNames.InboxDeadLetters);
            _ = sp.GetRequiredKeyedService<IDurableValue<string>>(DurableMessagingStateNames.InboxJobId);
            _ = sp.GetRequiredKeyedService<IDurableValue<DurableJob>>(DurableMessagingStateNames.InboxJobHandle);
            _ = sp.GetRequiredKeyedService<IDurableValue<string>>(DurableMessagingStateNames.InboxCompletedJobId);
            _ = sp.GetRequiredKeyedService<IDurableValue<long>>(DurableMessagingStateNames.InboxJobSequence);
            _ = sp.GetRequiredService<IDurableOutbox>();
            return new DurableInbox(
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>(DurableMessagingStateNames.Inbox),
                sp.GetServices<IInboxHandler>(),
                options.MaxCapacity);
        });
        services.TryAddScoped<IDurableInbox>(sp => sp.GetRequiredService<DurableInbox>());

        services.AddScoped<IDurableOutbox, JournaledTestOutbox>();
        services.TryAddScoped<IDurableMessagingDiagnostics, DurableMessagingDiagnostics>();
        services.TryAddScoped(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DurableJobsOptions>>().Value;
            var completedRetentionPeriod = TimeSpan.FromMinutes(10);
            var abandonedRetentionPeriod = options.JobStatusPollInterval <= TimeSpan.MaxValue / 4
                ? options.JobStatusPollInterval * 4
                : TimeSpan.MaxValue;
            return new DurableMessagingPumpResults(
                sp.GetRequiredKeyedService<TimeProvider>(DurableJobTimeProviderNames.DurableJobs),
                completedRetentionPeriod,
                TimeSpan.FromTicks(Math.Max(completedRetentionPeriod.Ticks, abandonedRetentionPeriod.Ticks)),
                maxRetainedEntries: 65_536);
        });
        services.TryAddScoped<DurableMessagingGrainParticipant>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IJournaledGrainParticipant, DurableMessagingGrainParticipant>());
    }
}
