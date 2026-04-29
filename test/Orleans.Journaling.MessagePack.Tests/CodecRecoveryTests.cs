using Microsoft.Extensions.Logging;
using MessagePack;
using Orleans.Journaling.MessagePack;
using Orleans.Journaling.Tests;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Journaling.MessagePack.Tests;

[TestCategory("BVT")]
public sealed class CodecRecoveryTests : StateMachineTestBase
{
    private static readonly MessagePackSerializerOptions SerializerOptions = MessagePackSerializerOptions.Standard;

    [Fact]
    public async Task MessagePackCodec_Dictionary_WriteAndRecover()
    {
        var storage = new VolatileStateMachineStorage(StateMachineLogFormatKeys.MessagePack);

        var sut = CreateTestSystemWithMessagePackCodec(storage);
        var dict = new DurableDictionary<string, int>("dict", sut.Manager, new MessagePackDictionaryEntryCodec<string, int>(SerializerOptions));
        await sut.Lifecycle.OnStart();

        dict.Add("alpha", 1);
        dict.Add("beta", 2);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var sut2 = CreateTestSystemWithMessagePackCodec(storage);
        var dict2 = new DurableDictionary<string, int>("dict", sut2.Manager, new MessagePackDictionaryEntryCodec<string, int>(SerializerOptions));
        await sut2.Lifecycle.OnStart();

        Assert.Equal(2, dict2.Count);
        Assert.Equal(1, dict2["alpha"]);
        Assert.Equal(2, dict2["beta"]);
    }

    private (IStateMachineManager Manager, IStateMachineStorage Storage, ILifecycleSubject Lifecycle) CreateTestSystemWithMessagePackCodec(IStateMachineStorage storage)
    {
        var stateMachineIdsCodec = new MessagePackDictionaryEntryCodec<string, ulong>(SerializerOptions);
        var retirementTrackerCodec = new MessagePackDictionaryEntryCodec<string, DateTime>(SerializerOptions);
        var manager = new StateMachineManager(
            storage,
            LoggerFactory.CreateLogger<StateMachineManager>(),
            Microsoft.Extensions.Options.Options.Create(ManagerOptions),
            stateMachineIdsCodec,
            retirementTrackerCodec,
            TimeProvider.System,
            new MessagePackLogFormat());
        var lifecycle = new TestGrainLifecycle(LoggerFactory.CreateLogger<TestGrainLifecycle>());
        (manager as ILifecycleParticipant<IGrainLifecycle>)?.Participate(lifecycle);
        return (manager, storage, lifecycle);
    }

    private sealed class TestGrainLifecycle(ILogger logger) : LifecycleSubject(logger), IGrainLifecycle
    {
        public void AddMigrationParticipant(IGrainMigrationParticipant participant) { }
        public void RemoveMigrationParticipant(IGrainMigrationParticipant participant) { }
    }
}
