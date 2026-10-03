using System.Reflection;
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
    public const string InboxJobName = "orleans.messaging.inbox-drain";

    private static readonly Func<IGrainContext?> ReadCurrentContext = typeof(IGrainContext).Assembly
        .GetType("Orleans.Runtime.RuntimeContext", throwOnError: true)!
        .GetProperty("Current")!.GetMethod!.CreateDelegate<Func<IGrainContext?>>();

    public static IGrainContext? CurrentGrainContext => ReadCurrentContext();

    public static Type GetImplementationType(string name) =>
        typeof(IDurableInbox).Assembly.GetType($"Orleans.DurableMessaging.{name}", throwOnError: true)!;

    public static void Add(IServiceCollection services, Action<DurableInboxOptions> configure)
    {
        var inboxType = GetImplementationType("DurableInbox");
        var extensionType = GetImplementationType("DurableInboxExtension");
        var instrumentsType = GetImplementationType("DurableMessagingInstruments");
        var pumpResultsType = GetImplementationType("DurableMessagingPumpResults");
        var configuratorType = GetImplementationType("DurableMessagingGrainTypeConfigurator");
        services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = "orleans-binary");
        services.AddOptions<DurableInboxOptions>().Configure(configure);
        services.TryAddSingleton(instrumentsType);
        services.TryAddScoped(extensionType, sp => CreateInstance(
            extensionType,
            sp.GetRequiredService<IGrainContext>(),
            sp.GetRequiredService<ITimerRegistry>(),
            sp.GetRequiredService<IJournaledStateManager>(),
            sp.GetRequiredService<SerializerSessionPool>(),
            sp.GetRequiredService(typeof(ILogger<>).MakeGenericType(extensionType)),
            sp.GetRequiredService(instrumentsType),
            sp.GetRequiredService(inboxType),
            GetDictionary<(GrainId, Guid), DurableEnvelope>(sp, "inbox"),
            GetDictionary<(GrainId, Guid), DateTimeOffset>(sp, "inbox-processed"),
            GetInternalDictionary<(GrainId, Guid)>(sp, "InboxMessageState", "inbox-message-state"),
            GetInternalDictionary<(GrainId, Guid)>(sp, "InboxDeadLetter", "inbox-dead-letters"),
            GetValue<string>(sp, "inbox-job-id"),
            GetValue<DurableJob>(sp, "inbox-job-handle"),
            GetValue<string>(sp, "inbox-completed-job-id"),
            GetValue<long>(sp, "inbox-job-sequence"),
            sp.GetRequiredService<IDurableOutbox>(),
            sp.GetRequiredService<ILocalDurableJobManager>(),
            sp.GetRequiredService<IDurableJobHandlerRegistry>(),
            sp.GetRequiredService(pumpResultsType),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredKeyedService<TimeProvider>(DurableJobTimeProviderNames.DurableJobs),
            sp.GetRequiredService<IOptions<DurableInboxOptions>>().Value));

        services.TryAddKeyedScoped<IGrainExtension>(
            typeof(IDurableInboxExtension),
            (sp, _) => (IGrainExtension)sp.GetRequiredService(extensionType));
        services.TryAddScoped(inboxType, sp =>
        {
            var options = sp.GetRequiredService<IOptions<DurableInboxOptions>>().Value;
            _ = GetDictionary<(GrainId, Guid), DateTimeOffset>(sp, "inbox-processed");
            _ = GetInternalDictionary<(GrainId, Guid)>(sp, "InboxMessageState", "inbox-message-state");
            _ = GetInternalDictionary<(GrainId, Guid)>(sp, "InboxDeadLetter", "inbox-dead-letters");
            _ = GetValue<string>(sp, "inbox-job-id");
            _ = GetValue<DurableJob>(sp, "inbox-job-handle");
            _ = GetValue<string>(sp, "inbox-completed-job-id");
            _ = GetValue<long>(sp, "inbox-job-sequence");
            _ = sp.GetRequiredService<IDurableOutbox>();
            return CreateInstance(inboxType,
                GetDictionary<(GrainId, Guid), DurableEnvelope>(sp, "inbox"),
                sp.GetServices<IInboxHandler>(), options.MaxCapacity);
        });
        services.TryAddScoped<IDurableInbox>(sp => (IDurableInbox)sp.GetRequiredService(inboxType));

        services.AddScoped<IDurableOutbox>(static sp =>
            new JournaledTestOutbox(sp.GetRequiredKeyedService<IDurableDictionary<Guid, DurableEnvelope>>("test-handler-output")));
        services.TryAddScoped(typeof(IDurableMessagingDiagnostics), GetImplementationType("DurableMessagingDiagnostics"));
        services.TryAddScoped(pumpResultsType, sp =>
        {
            var options = sp.GetRequiredService<IOptions<DurableJobsOptions>>().Value;
            var completedRetentionPeriod = TimeSpan.FromMinutes(10);
            var abandonedRetentionPeriod = options.JobStatusPollInterval <= TimeSpan.MaxValue / 4
                ? options.JobStatusPollInterval * 4
                : TimeSpan.MaxValue;
            return CreateInstance(pumpResultsType,
                sp.GetRequiredKeyedService<TimeProvider>(DurableJobTimeProviderNames.DurableJobs),
                completedRetentionPeriod,
                TimeSpan.FromTicks(Math.Max(completedRetentionPeriod.Ticks, abandonedRetentionPeriod.Ticks)),
                65_536);
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IConfigureGrainTypeComponents), configuratorType));
    }

    private static IDurableDictionary<TKey, TValue> GetDictionary<TKey, TValue>(IServiceProvider services, string stateName)
        where TKey : notnull =>
        services.GetRequiredKeyedService<IDurableDictionary<TKey, TValue>>($"__orleans.durable-messaging.{stateName}");

    private static object GetInternalDictionary<TKey>(IServiceProvider services, string valueType, string stateName) =>
        services.GetRequiredKeyedService(typeof(IDurableDictionary<,>).MakeGenericType(typeof(TKey), GetImplementationType(valueType)),
            $"__orleans.durable-messaging.{stateName}");

    private static IDurableValue<T> GetValue<T>(IServiceProvider services, string stateName) =>
        services.GetRequiredKeyedService<IDurableValue<T>>($"__orleans.durable-messaging.{stateName}");

    private static object CreateInstance(Type type, params object?[] arguments) =>
        Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DoNotWrapExceptions,
            binder: null, arguments, culture: null)!;
}
