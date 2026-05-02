using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using MessagePack;
using Orleans.Core;
using Orleans.Journaling.MessagePack;
using Orleans.Journaling.Tests;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Journaling.MessagePack.Tests;

[TestCategory("BVT")]
public sealed class CodecRecoveryTests : JournalingTestBase
{
    private static readonly MessagePackSerializerOptions SerializerOptions = MessagePackSerializerOptions.Standard;

    [Fact]
    public async Task MessagePackCodec_Dictionary_WriteAndRecover()
    {
        var storage = new VolatileLogStorage();

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

    [Fact]
    public async Task MessagePackCodec_ListQueueSetAndValue_WriteAndRecover()
    {
        var storage = new VolatileLogStorage();

        var sut = CreateTestSystemWithMessagePackCodec(storage);
        var list = new DurableList<string>("list", sut.Manager, new MessagePackListOperationCodec<string>(SerializerOptions));
        var queue = new DurableQueue<string>("queue", sut.Manager, new MessagePackQueueOperationCodec<string>(SerializerOptions));
        var set = new DurableSet<string>("set", sut.Manager, new MessagePackSetOperationCodec<string>(SerializerOptions));
        var value = new DurableValue<int>("value", sut.Manager, new MessagePackValueOperationCodec<int>(SerializerOptions));
        await sut.Lifecycle.OnStart();

        list.Add("one");
        list.Add("two");
        queue.Enqueue("first");
        queue.Enqueue("second");
        set.Add("a");
        set.Add("b");
        value.Value = 42;
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var sut2 = CreateTestSystemWithMessagePackCodec(storage);
        var list2 = new DurableList<string>("list", sut2.Manager, new MessagePackListOperationCodec<string>(SerializerOptions));
        var queue2 = new DurableQueue<string>("queue", sut2.Manager, new MessagePackQueueOperationCodec<string>(SerializerOptions));
        var set2 = new DurableSet<string>("set", sut2.Manager, new MessagePackSetOperationCodec<string>(SerializerOptions));
        var value2 = new DurableValue<int>("value", sut2.Manager, new MessagePackValueOperationCodec<int>(SerializerOptions));
        await sut2.Lifecycle.OnStart();

        Assert.Equal(["one", "two"], list2);
        Assert.Equal(2, queue2.Count);
        Assert.Equal("first", queue2.Dequeue());
        Assert.Equal("second", queue2.Dequeue());
        Assert.True(set2.SetEquals(["a", "b"]));
        Assert.Equal(42, value2.Value);
    }

    [Fact]
    public async Task MessagePackCodec_StateAndTcs_WriteAndRecover()
    {
        var storage = new VolatileLogStorage();

        var sut = CreateTestSystemWithMessagePackCodec(storage);
        var state = new DurableState<string>("state", sut.Manager, new MessagePackStateOperationCodec<string>(SerializerOptions));
        var tcs = new DurableTaskCompletionSource<int>(
            "tcs",
            sut.Manager,
            new MessagePackTcsOperationCodec<int>(SerializerOptions),
            Copier<int>(),
            Copier<Exception>());
        await sut.Lifecycle.OnStart();

        ((IStorage<string>)state).State = "state-value";
        Assert.True(tcs.TrySetResult(17));
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var sut2 = CreateTestSystemWithMessagePackCodec(storage);
        var state2 = new DurableState<string>("state", sut2.Manager, new MessagePackStateOperationCodec<string>(SerializerOptions));
        var tcs2 = new DurableTaskCompletionSource<int>(
            "tcs",
            sut2.Manager,
            new MessagePackTcsOperationCodec<int>(SerializerOptions),
            Copier<int>(),
            Copier<Exception>());
        await sut2.Lifecycle.OnStart();

        Assert.Equal("state-value", ((IStorage<string>)state2).State);
        Assert.Equal(DurableTaskCompletionSourceStatus.Completed, tcs2.State.Status);
        Assert.Equal(17, tcs2.State.Value);
        Assert.Equal(17, await tcs2.Task);
    }

    private (IStateMachineManager Manager, ILogStorage Storage, ILifecycleSubject Lifecycle) CreateTestSystemWithMessagePackCodec(ILogStorage storage)
    {
        var logStreamIdsCodec = new MessagePackDictionaryOperationCodec<string, ulong>(SerializerOptions);
        var retirementTrackerCodec = new MessagePackDictionaryOperationCodec<string, DateTime>(SerializerOptions);
        var manager = new LogStateMachineManager(
            storage,
            LoggerFactory.CreateLogger<LogStateMachineManager>(),
            Microsoft.Extensions.Options.Options.Create(ManagerOptions),
            logStreamIdsCodec,
            retirementTrackerCodec,
            TimeProvider.System,
            new MessagePackLogFormat());
        var lifecycle = new TestGrainLifecycle(LoggerFactory.CreateLogger<TestGrainLifecycle>());
        (manager as ILifecycleParticipant<IGrainLifecycle>)?.Participate(lifecycle);
        return (manager, storage, lifecycle);
    }

    private DeepCopier<T> Copier<T>() => ServiceProvider.GetRequiredService<DeepCopier<T>>();

    private sealed class TestGrainLifecycle(ILogger logger) : LifecycleSubject(logger), IGrainLifecycle
    {
        public void AddMigrationParticipant(IGrainMigrationParticipant participant) { }
        public void RemoveMigrationParticipant(IGrainMigrationParticipant participant) { }
    }
}
