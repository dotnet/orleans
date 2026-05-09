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
    protected readonly StateManagerOptions ManagerOptions = new();

    protected JournalingTestBase()
    {
        var services = new ServiceCollection();

        services.AddSerializer();
        services.AddLogging(builder => builder.AddConsole());

        ServiceProvider = services.BuildServiceProvider();
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
    internal (IStateManager Manager, IJournalStorage Storage, ILifecycleSubject Lifecycle)
        CreateTestSystem(IJournalStorage? storage = null, TimeProvider? provider = null, IJournalFormat? journalFormat = null, string? journalFormatKey = null)
    {
        storage ??= CreateStorage();
        provider ??= TimeProvider.System;

        var logger = LoggerFactory.CreateLogger<JournaledStateManager>();
        var stringCodec = new OrleansJournalValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool);
        var uint64Codec = new OrleansJournalValueCodec<ulong>(CodecProvider.GetCodec<ulong>(), SessionPool);
        var dateTimeCodec = new OrleansJournalValueCodec<DateTime>(CodecProvider.GetCodec<DateTime>(), SessionPool);
        var journalStreamIdsCodec = new OrleansBinaryDictionaryOperationCodec<string, ulong>(stringCodec, uint64Codec);
        var retirementTrackerCodec = new OrleansBinaryDictionaryOperationCodec<string, DateTime>(stringCodec, dateTimeCodec);
        var manager = new JournaledStateManager(storage, logger, Options.Create(ManagerOptions), journalStreamIdsCodec, retirementTrackerCodec, provider, journalFormat, journalFormatKey);
        var lifecycle = new GrainLifecycle(LoggerFactory.CreateLogger<GrainLifecycle>());
        (manager as ILifecycleParticipant<IGrainLifecycle>)?.Participate(lifecycle);
        return (manager, storage, lifecycle);
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
