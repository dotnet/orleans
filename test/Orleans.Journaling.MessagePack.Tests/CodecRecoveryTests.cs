using Microsoft.Extensions.Logging;
using MessagePack;
using Orleans.Journaling.MessagePack;
using Orleans.Journaling.Tests;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Journaling.MessagePack.Tests;

[TestCategory("BVT")]
public sealed class CodecRecoveryTests : JournalingTestBase
{
    private static readonly MessagePackSerializerOptions SerializerOptions = MessagePackSerializerOptions.Standard;

    [Fact]
    public async Task MessagePackCodec_Dictionary_WriteAndRecover()
    {
        var storage = new VolatileLogStorage(LogFormatKeys.MessagePack);

        var sut = CreateTestSystemWithMessagePackCodec(storage);
        var dict = new DurableDictionary<string, int>("dict", sut.Manager, new MessagePackDictionaryOperationCodec<string, int>(SerializerOptions));
        await sut.Lifecycle.OnStart();

        dict.Add("alpha", 1);
        dict.Add("beta", 2);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var sut2 = CreateTestSystemWithMessagePackCodec(storage);
        var dict2 = new DurableDictionary<string, int>("dict", sut2.Manager, new MessagePackDictionaryOperationCodec<string, int>(SerializerOptions));
        await sut2.Lifecycle.OnStart();

        Assert.Equal(2, dict2.Count);
        Assert.Equal(1, dict2["alpha"]);
        Assert.Equal(2, dict2["beta"]);
    }

    private (ILogManager Manager, ILogStorage Storage, ILifecycleSubject Lifecycle) CreateTestSystemWithMessagePackCodec(ILogStorage storage)
    {
        var logStreamIdsCodec = new MessagePackDictionaryOperationCodec<string, ulong>(SerializerOptions);
        var retirementTrackerCodec = new MessagePackDictionaryOperationCodec<string, DateTime>(SerializerOptions);
        var manager = new LogManager(
            storage,
            LoggerFactory.CreateLogger<LogManager>(),
            Microsoft.Extensions.Options.Options.Create(ManagerOptions),
            logStreamIdsCodec,
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
