using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Microsoft.Extensions.Time.Testing;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Base class for journaling tests with common setup
/// </summary>
public abstract class JournalingTestBase
{
    protected readonly ServiceProvider ServiceProvider;
    protected readonly SerializerSessionPool SessionPool;
    protected readonly ICodecProvider CodecProvider;
    protected readonly ILoggerFactory LoggerFactory;
    protected readonly JournaledStateManagerOptions ManagerOptions = new()
    {
        JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey
    };

    protected JournalingTestBase()
    {
        ServiceProvider = CreateServiceProvider();
        SessionPool = ServiceProvider.GetRequiredService<SerializerSessionPool>();
        CodecProvider = ServiceProvider.GetRequiredService<ICodecProvider>();
        LoggerFactory = ServiceProvider.GetRequiredService<ILoggerFactory>();
    }

    /// <summary>
    /// Creates an in-memory storage for testing
    /// </summary>
    protected virtual IJournalStorage CreateStorage()
    {
        return new VolatileJournalStorage();
    }

    /// <summary>
    /// Creates a journal manager with in-memory storage
    /// </summary>
    internal (IJournaledStateManager Manager, IJournalStorage Storage, ILifecycleSubject Lifecycle)
        CreateTestSystem(IJournalStorage? storage = null, TimeProvider? provider = null, IJournalFormat? journalFormat = null)
    {
        storage ??= CreateStorage();
        provider ??= TimeProvider.System;

        var serviceProvider = journalFormat is null ? ServiceProvider : CreateServiceProvider(journalFormat);
        var options = journalFormat is null
            ? ManagerOptions
            : new JournaledStateManagerOptions
            {
                JournalFormatKey = journalFormat.FormatKey,
                RetirementGracePeriod = ManagerOptions.RetirementGracePeriod
            };
        if (storage is VolatileJournalStorage volatileStorage)
        {
            volatileStorage.SetConfiguredJournalFormatKey(options.JournalFormatKey);
        }

        var shared = new JournaledStateManagerShared(
            serviceProvider.GetRequiredService<ILogger<JournaledStateManager>>(),
            Options.Create(options),
            provider,
            storage,
            serviceProvider);
        var manager = new JournaledStateManager(shared);
        var lifecycle = new GrainLifecycle(serviceProvider.GetRequiredService<ILogger<GrainLifecycle>>());
        (manager as ILifecycleParticipant<IGrainLifecycle>)?.Participate(lifecycle);
        return (manager, storage, lifecycle);
    }

    private static ServiceProvider CreateServiceProvider(IJournalFormat? journalFormat = null)
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddLogging(builder => builder.AddConsole());
        var customKey = journalFormat is null ? null : JournalFormatServices.ValidateJournalFormatKey(journalFormat.FormatKey);
        ConfigureBinaryJournalingServices(
            services,
            string.Equals(customKey, OrleansBinaryJournalFormat.JournalFormatKey, StringComparison.Ordinal) ? journalFormat : null);

        if (journalFormat is not null && !string.Equals(customKey, OrleansBinaryJournalFormat.JournalFormatKey, StringComparison.Ordinal))
        {
            services.AddKeyedSingleton<IJournalFormat>(customKey!, journalFormat);
            services.AddKeyedSingleton(typeof(IDurableDictionaryCommandCodec<,>), customKey!, typeof(OrleansBinaryDurableDictionaryCommandCodec<,>));
        }

        return services.BuildServiceProvider();
    }

    protected static void ConfigureBinaryJournalingServices(IServiceCollection services, IJournalFormat? journalFormat = null)
    {
        services.AddSingleton<OrleansBinaryJournalFormat>();
        if (journalFormat is null)
        {
            services.AddKeyedSingleton<IJournalFormat>(
                OrleansBinaryJournalFormat.JournalFormatKey,
                static (sp, _) => sp.GetRequiredService<OrleansBinaryJournalFormat>());
        }
        else
        {
            services.AddKeyedSingleton<IJournalFormat>(OrleansBinaryJournalFormat.JournalFormatKey, journalFormat);
        }

        services.AddKeyedSingleton(
            typeof(IDurableDictionaryCommandCodec<,>),
            OrleansBinaryJournalFormat.JournalFormatKey,
            typeof(OrleansBinaryDurableDictionaryCommandCodec<,>));
    }

    private class GrainLifecycle(ILogger logger) : LifecycleSubject(logger), IGrainLifecycle
    {
        private static readonly ImmutableDictionary<int, string> StageNames = GetStageNames(typeof(GrainLifecycleStage));

        public void AddMigrationParticipant(IGrainMigrationParticipant participant) { }
        public void RemoveMigrationParticipant(IGrainMigrationParticipant participant) { }

        protected override string GetStageName(int stage)
        {
            if (StageNames.TryGetValue(stage, out var result)) return result;
            return base.GetStageName(stage);
        }
    }
}
