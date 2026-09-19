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
        services.AddScoped(GetImplementationType("InboxJournalState"), sp =>
        {
            var manager = sp.GetRequiredService<IJournaledStateManager>();
            var state = (IStateMachine)CreateInstance(GetImplementationType("InboxJournalState"), manager);
            manager.RegisterStateMachine("__orleans.durable-messaging.inbox", state);
            return state;
        });
        services.AddKeyedScoped<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>("__orleans.durable-messaging.inbox",
            (sp, _) => (IDurableDictionary<(GrainId, Guid), DurableEnvelope>)sp.GetRequiredService(GetImplementationType("InboxJournalState")));
        RegisterDictionary<(GrainId, Guid), DateTimeOffset>(services, "inbox-processed");
        RegisterInternalDictionary<(GrainId, Guid)>(services, "InboxMessageState", "inbox-message-state");
        RegisterInternalDictionary<(GrainId, Guid)>(services, "InboxDeadLetter", "inbox-dead-letters");
        services.AddStateMachine<IDurableDictionary<Guid, DurableEffect>, ObservedJournalDictionary<Guid, DurableEffect>>(
            static (sp, _) => new(sp.GetRequiredService<IJournaledStateManager>()));
        services.AddStateMachine<IDurableValue<int>, ObservedJournalValue<int>>(
            static (sp, _) => new(sp.GetRequiredService<IJournaledStateManager>()));

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
            sp.GetRequiredService<IOptions<DurableInboxOptions>>().Value,
            sp.GetRequiredService(GetImplementationType("InboxJournalState"))));

        services.TryAddKeyedScoped<IGrainExtension>(
            typeof(IDurableInboxExtension),
            (sp, _) => (IGrainExtension)sp.GetRequiredService(extensionType));
        services.TryAddScoped(inboxType, sp =>
        {
            var options = sp.GetRequiredService<IOptions<DurableInboxOptions>>().Value;
            _ = GetInternalDictionary<(GrainId, Guid)>(sp, "InboxMessageState", "inbox-message-state");
            _ = GetInternalDictionary<(GrainId, Guid)>(sp, "InboxDeadLetter", "inbox-dead-letters");
            _ = GetValue<string>(sp, "inbox-job-id");
            _ = GetValue<DurableJob>(sp, "inbox-job-handle");
            _ = GetValue<string>(sp, "inbox-completed-job-id");
            _ = GetValue<long>(sp, "inbox-job-sequence");
            _ = sp.GetRequiredService<IDurableOutbox>();
            return CreateInstance(
                inboxType,
                GetDictionary<(GrainId, Guid), DurableEnvelope>(sp, "inbox"),
                sp.GetServices<IInboxHandler>(),
                options.MaxCapacity);
        });
        services.TryAddScoped<IDurableInbox>(sp => (IDurableInbox)sp.GetRequiredService(inboxType));

        services.AddStateMachine<IDurableOutbox, JournaledTestOutbox>();
        services.AddScoped<IDurableOutbox>(static sp =>
            sp.GetRequiredKeyedService<IDurableOutbox>("test-handler-output"));
        services.AddKeyedScoped<IDurableDictionary<Guid, DurableEnvelope>>("test-handler-output", (sp, _) =>
            (JournaledTestOutbox)sp.GetRequiredService<IDurableOutbox>());
        services.TryAddScoped(typeof(IDurableMessagingDiagnostics), GetImplementationType("DurableMessagingDiagnostics"));
        services.TryAddScoped(pumpResultsType, sp =>
        {
            var options = sp.GetRequiredService<IOptions<DurableJobsOptions>>().Value;
            var completedRetentionPeriod = TimeSpan.FromMinutes(10);
            var abandonedRetentionPeriod = options.JobStatusPollInterval <= TimeSpan.MaxValue / 4
                ? options.JobStatusPollInterval * 4
                : TimeSpan.MaxValue;
            return CreateInstance(
                pumpResultsType,
                sp.GetRequiredKeyedService<TimeProvider>(DurableJobTimeProviderNames.DurableJobs),
                completedRetentionPeriod,
                TimeSpan.FromTicks(Math.Max(completedRetentionPeriod.Ticks, abandonedRetentionPeriod.Ticks)),
                65_536);
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IConfigureGrainTypeComponents), configuratorType));
    }

    public static IStateMachine CreateStandardDictionary<TKey, TValue>(IJournaledStateManager manager) where TKey : notnull
    {
        var type = typeof(IStateMachine).Assembly.GetType("Orleans.Journaling.DurableDictionary`2", throwOnError: true)!
            .MakeGenericType(typeof(TKey), typeof(TValue));
        return (IStateMachine)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DoNotWrapExceptions,
            binder: null, args: [manager.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<TKey, TValue>>()], culture: null)!;
    }

    public static IStateMachine CreateDeferredDictionary<TKey, TValue>(IJournaledStateManager manager) where TKey : notnull =>
        (IStateMachine)CreateInstance(GetImplementationType("DeferredJournaledDictionary`2").MakeGenericType(typeof(TKey), typeof(TValue)), manager);

    public static IStateMachine CreateDeferredValue<T>(IJournaledStateManager manager) =>
        (IStateMachine)CreateInstance(GetImplementationType("DeferredJournaledValue`1").MakeGenericType(typeof(T)), manager);

    private static void RegisterDictionary<TKey, TValue>(IServiceCollection services, string name) where TKey : notnull =>
        services.AddKeyedScoped<IDurableDictionary<TKey, TValue>>("__orleans.durable-messaging." + name, (sp, key) =>
        {
            var manager = sp.GetRequiredService<IJournaledStateManager>();
            var state = CreateDeferredDictionary<TKey, TValue>(manager);
            manager.RegisterStateMachine((string)key!, state);
            return (IDurableDictionary<TKey, TValue>)state;
        });

    private static void RegisterInternalDictionary<TKey>(IServiceCollection services, string type, string name) where TKey : notnull
    {
        var valueType = GetImplementationType(type);
        services.AddKeyedScoped(typeof(IDurableDictionary<,>).MakeGenericType(typeof(TKey), valueType),
            "__orleans.durable-messaging." + name, (sp, key) =>
            {
                var manager = sp.GetRequiredService<IJournaledStateManager>();
                var state = (IStateMachine)CreateInstance(GetImplementationType("DeferredJournaledDictionary`2").MakeGenericType(typeof(TKey), valueType), manager);
                manager.RegisterStateMachine((string)key!, state);
                return state;
            });
    }

    private static IDurableDictionary<TKey, TValue> GetDictionary<TKey, TValue>(IServiceProvider services, string stateName)
        where TKey : notnull =>
        services.GetRequiredKeyedService<IDurableDictionary<TKey, TValue>>($"__orleans.durable-messaging.{stateName}");

    private static object GetInternalDictionary<TKey>(IServiceProvider services, string valueType, string stateName) =>
        services.GetRequiredKeyedService(
            typeof(IDurableDictionary<,>).MakeGenericType(typeof(TKey), GetImplementationType(valueType)),
            $"__orleans.durable-messaging.{stateName}");

    private static IDurableValue<T> GetValue<T>(IServiceProvider services, string stateName) =>
        services.GetRequiredKeyedService<IDurableValue<T>>($"__orleans.durable-messaging.{stateName}");

    private static object CreateInstance(Type type, params object?[] arguments) =>
        Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DoNotWrapExceptions,
            binder: null,
            arguments,
            culture: null)!;
}
