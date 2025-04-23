using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Base class for journaling tests with common setup
/// </summary>
public abstract class StateMachineTestBase
{
    protected readonly ServiceProvider ServiceProvider;
    protected readonly SerializerSessionPool SessionPool;
    protected readonly ICodecProvider CodecProvider;
    protected readonly ILoggerFactory LoggerFactory;

    protected StateMachineTestBase()
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
    protected virtual IStateMachineStorage CreateStorage()
    {
        return new VolatileStateMachineStorage();
    }

    /// <summary>
    /// Creates a state machine manager with in-memory storage
    /// </summary>
    internal (IStateMachineManager Manager, IStateMachineStorage Storage, ILifecycleSubject Lifecycle) CreateTestSystem(IStateMachineStorage? storage = null)
    {
        storage ??= CreateStorage();
        var logger = LoggerFactory.CreateLogger<StateMachineManager>();
        var manager = new StateMachineManager(storage, logger, SessionPool);
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
