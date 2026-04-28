using Microsoft.Extensions.Logging;
using Orleans.Journaling.Protobuf;
using Orleans.Journaling.Tests;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Journaling.Protobuf.Tests;

/// <summary>
/// Tests that verify same-format recovery for Protobuf journaling and the Orleans binary compatibility baseline.
/// </summary>
[TestCategory("BVT")]
public class CodecRecoveryTests : StateMachineTestBase
{
    /// <summary>
    /// Writes data with the Orleans binary codec, then reads it back.
    /// This is the baseline backward compatibility test.
    /// </summary>
    [Fact]
    public async Task OrleansBinaryCodec_WriteAndRecover()
    {
        var storage = CreateStorage();

        var sut = CreateTestSystem(storage);
        var keyCodec = new OrleansLogDataCodec<string>(CodecProvider.GetCodec<string>(), SessionPool);
        var valueCodec = new OrleansLogDataCodec<int>(CodecProvider.GetCodec<int>(), SessionPool);
        var dict = new DurableDictionary<string, int>("dict", sut.Manager, new OrleansBinaryDictionaryEntryCodec<string, int>(keyCodec, valueCodec));
        await sut.Lifecycle.OnStart();

        dict.Add("alpha", 1);
        dict.Add("beta", 2);
        dict.Add("gamma", 3);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var sut2 = CreateTestSystem(storage);
        var keyCodec2 = new OrleansLogDataCodec<string>(CodecProvider.GetCodec<string>(), SessionPool);
        var valueCodec2 = new OrleansLogDataCodec<int>(CodecProvider.GetCodec<int>(), SessionPool);
        var dict2 = new DurableDictionary<string, int>("dict", sut2.Manager, new OrleansBinaryDictionaryEntryCodec<string, int>(keyCodec2, valueCodec2));
        await sut2.Lifecycle.OnStart();

        Assert.Equal(3, dict2.Count);
        Assert.Equal(1, dict2["alpha"]);
        Assert.Equal(2, dict2["beta"]);
        Assert.Equal(3, dict2["gamma"]);
    }

    /// <summary>
    /// Writes a dictionary with the Protobuf codec, then reads it back.
    /// </summary>
    [Fact]
    public async Task ProtobufCodec_Dictionary_WriteAndRecover()
    {
        var storage = CreateProtobufStorage();

        var sut = CreateTestSystemWithProtobufCodec(storage);
        var dict = new DurableDictionary<string, int>("dict", sut.Manager,
            new ProtobufDictionaryEntryCodec<string, int>(CreateConverter<string>(), CreateConverter<int>()));
        await sut.Lifecycle.OnStart();

        dict.Add("alpha", 1);
        dict.Add("beta", 2);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        Assert.NotEmpty(storage.Segments.Single());

        var sut2 = CreateTestSystemWithProtobufCodec(storage);
        var dict2 = new DurableDictionary<string, int>("dict", sut2.Manager,
            new ProtobufDictionaryEntryCodec<string, int>(CreateConverter<string>(), CreateConverter<int>()));
        await sut2.Lifecycle.OnStart();

        Assert.Equal(2, dict2.Count);
        Assert.Equal(1, dict2["alpha"]);
        Assert.Equal(2, dict2["beta"]);
    }

    /// <summary>
    /// Writes a list with the Protobuf codec, then reads it back.
    /// </summary>
    [Fact]
    public async Task ProtobufCodec_DurableList_WriteAndRecover()
    {
        var storage = CreateProtobufStorage();

        var sut = CreateTestSystemWithProtobufCodec(storage);
        var list = new DurableList<string>("list", sut.Manager,
            new ProtobufListEntryCodec<string>(CreateConverter<string>()));
        await sut.Lifecycle.OnStart();

        list.Add("one");
        list.Add("two");
        list.Add("three");
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var sut2 = CreateTestSystemWithProtobufCodec(storage);
        var list2 = new DurableList<string>("list", sut2.Manager,
            new ProtobufListEntryCodec<string>(CreateConverter<string>()));
        await sut2.Lifecycle.OnStart();

        Assert.Equal(3, list2.Count);
        Assert.Equal("one", list2[0]);
        Assert.Equal("two", list2[1]);
        Assert.Equal("three", list2[2]);
    }

    /// <summary>
    /// Writes a value with the Protobuf codec, then reads it back.
    /// </summary>
    [Fact]
    public async Task ProtobufCodec_DurableValue_WriteAndRecover()
    {
        var storage = CreateProtobufStorage();

        var sut = CreateTestSystemWithProtobufCodec(storage);
        var value = new DurableValue<int>("val", sut.Manager,
            new ProtobufValueEntryCodec<int>(CreateConverter<int>()));
        await sut.Lifecycle.OnStart();

        value.Value = 42;
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var sut2 = CreateTestSystemWithProtobufCodec(storage);
        var value2 = new DurableValue<int>("val", sut2.Manager,
            new ProtobufValueEntryCodec<int>(CreateConverter<int>()));
        await sut2.Lifecycle.OnStart();

        Assert.Equal(42, value2.Value);
    }

    /// <summary>
    /// Writes a set with the Protobuf codec, then reads it back.
    /// </summary>
    [Fact]
    public async Task ProtobufCodec_DurableSet_WriteAndRecover()
    {
        var storage = CreateProtobufStorage();

        var sut = CreateTestSystemWithProtobufCodec(storage);
        var set = new DurableSet<string>("set", sut.Manager,
            new ProtobufSetEntryCodec<string>(CreateConverter<string>()));
        await sut.Lifecycle.OnStart();

        set.Add("a");
        set.Add("b");
        set.Add("c");
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var sut2 = CreateTestSystemWithProtobufCodec(storage);
        var set2 = new DurableSet<string>("set", sut2.Manager,
            new ProtobufSetEntryCodec<string>(CreateConverter<string>()));
        await sut2.Lifecycle.OnStart();

        Assert.Equal(3, set2.Count);
        Assert.Contains("a", (IReadOnlySet<string>)set2);
        Assert.Contains("b", (IReadOnlySet<string>)set2);
        Assert.Contains("c", (IReadOnlySet<string>)set2);
    }

    /// <summary>
    /// Writes a queue with the Protobuf codec, then reads it back.
    /// </summary>
    [Fact]
    public async Task ProtobufCodec_DurableQueue_WriteAndRecover()
    {
        var storage = CreateProtobufStorage();

        var sut = CreateTestSystemWithProtobufCodec(storage);
        var queue = new DurableQueue<string>("queue", sut.Manager,
            new ProtobufQueueEntryCodec<string>(CreateConverter<string>()));
        await sut.Lifecycle.OnStart();

        queue.Enqueue("first");
        queue.Enqueue("second");
        queue.Enqueue("third");
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var sut2 = CreateTestSystemWithProtobufCodec(storage);
        var queue2 = new DurableQueue<string>("queue", sut2.Manager,
            new ProtobufQueueEntryCodec<string>(CreateConverter<string>()));
        await sut2.Lifecycle.OnStart();

        Assert.Equal(3, queue2.Count);
        Assert.Equal("first", queue2.Dequeue());
        Assert.Equal("second", queue2.Dequeue());
        Assert.Equal("third", queue2.Dequeue());
    }

    private ProtobufValueConverter<T> CreateConverter<T>()
        => ProtobufValueConverter<T>.IsNativeType
            ? new ProtobufValueConverter<T>()
            : new ProtobufValueConverter<T>(new OrleansLogDataCodec<T>(CodecProvider.GetCodec<T>(), SessionPool));

    internal (IStateMachineManager Manager, IStateMachineStorage Storage, ILifecycleSubject Lifecycle) CreateTestSystemWithProtobufCodec(IStateMachineStorage? storage = null)
    {
        storage ??= CreateProtobufStorage();

        var stringConverter = CreateConverter<string>();
        var ulongConverter = CreateConverter<ulong>();
        var dateTimeConverter = CreateConverter<DateTime>();

        var stateMachineIdsCodec = new ProtobufDictionaryEntryCodec<string, ulong>(stringConverter, ulongConverter);
        var retirementTrackerCodec = new ProtobufDictionaryEntryCodec<string, DateTime>(stringConverter, dateTimeConverter);
        var manager = new StateMachineManager(storage, LoggerFactory.CreateLogger<StateMachineManager>(), Microsoft.Extensions.Options.Options.Create(ManagerOptions), stateMachineIdsCodec, retirementTrackerCodec, TimeProvider.System);
        var lifecycle = new TestGrainLifecycle(LoggerFactory.CreateLogger<TestGrainLifecycle>());
        (manager as ILifecycleParticipant<IGrainLifecycle>)?.Participate(lifecycle);
        return (manager, storage, lifecycle);
    }

    private static VolatileStateMachineStorage CreateProtobufStorage() => new(new ProtobufLogExtentCodec());

    private sealed class TestGrainLifecycle(ILogger logger) : LifecycleSubject(logger), IGrainLifecycle
    {
        public void AddMigrationParticipant(IGrainMigrationParticipant participant) { }
        public void RemoveMigrationParticipant(IGrainMigrationParticipant participant) { }
    }
}
