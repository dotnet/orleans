using Microsoft.Extensions.Logging;
using Orleans.Journaling.Json;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests that verify backward compatibility and migration between serialization formats.
/// </summary>
[TestCategory("BVT")]
public class CodecMigrationTests : StateMachineTestBase
{
    /// <summary>
    /// Writes data with the Orleans binary codec, then reads it back.
    /// This is the baseline backward compatibility test.
    /// </summary>
    [Fact]
    public async Task OrleansBinaryCodec_WriteAndRecover()
    {
        var storage = CreateStorage();

        // Write phase
        var sut = CreateTestSystem(storage);
        var keyCodec = new OrleansLogDataCodec<string>(CodecProvider.GetCodec<string>(), SessionPool);
        var valueCodec = new OrleansLogDataCodec<int>(CodecProvider.GetCodec<int>(), SessionPool);
        var dict = new DurableDictionary<string, int>("dict", sut.Manager, new OrleansBinaryDictionaryEntryCodec<string, int>(keyCodec, valueCodec));
        await sut.Lifecycle.OnStart();

        dict.Add("alpha", 1);
        dict.Add("beta", 2);
        dict.Add("gamma", 3);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        // Recovery phase — new manager, same storage
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
    /// Writes data with the JSON codec, then reads it back.
    /// Verifies the JSON format round-trips correctly.
    /// </summary>
    [Fact]
    public async Task JsonCodec_WriteAndRecover()
    {
        var storage = CreateStorage();
        var jsonOptions = new System.Text.Json.JsonSerializerOptions();

        // Write phase
        var sut = CreateTestSystemWithJsonCodec(storage, jsonOptions);
        var dict = new DurableDictionary<string, int>("dict", sut.Manager, new OrleansBinaryDictionaryEntryCodec<string, int>(new JsonLogDataCodec<string>(jsonOptions), new JsonLogDataCodec<int>(jsonOptions)));
        await sut.Lifecycle.OnStart();

        dict.Add("alpha", 1);
        dict.Add("beta", 2);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        // Recovery phase
        var sut2 = CreateTestSystemWithJsonCodec(storage, jsonOptions);
        var dict2 = new DurableDictionary<string, int>("dict", sut2.Manager, new OrleansBinaryDictionaryEntryCodec<string, int>(new JsonLogDataCodec<string>(jsonOptions), new JsonLogDataCodec<int>(jsonOptions)));
        await sut2.Lifecycle.OnStart();

        Assert.Equal(2, dict2.Count);
        Assert.Equal(1, dict2["alpha"]);
        Assert.Equal(2, dict2["beta"]);
    }

    /// <summary>
    /// Writes data with the JSON codec, then verifies DurableList round-trips.
    /// </summary>
    [Fact]
    public async Task JsonCodec_DurableList_WriteAndRecover()
    {
        var storage = CreateStorage();
        var jsonOptions = new System.Text.Json.JsonSerializerOptions();

        // Write phase
        var sut = CreateTestSystemWithJsonCodec(storage, jsonOptions);
        var list = new DurableList<string>("list", sut.Manager, new OrleansBinaryListEntryCodec<string>(new JsonLogDataCodec<string>(jsonOptions)));
        await sut.Lifecycle.OnStart();

        list.Add("one");
        list.Add("two");
        list.Add("three");
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        // Recovery phase
        var sut2 = CreateTestSystemWithJsonCodec(storage, jsonOptions);
        var list2 = new DurableList<string>("list", sut2.Manager, new OrleansBinaryListEntryCodec<string>(new JsonLogDataCodec<string>(jsonOptions)));
        await sut2.Lifecycle.OnStart();

        Assert.Equal(3, list2.Count);
        Assert.Equal("one", list2[0]);
        Assert.Equal("two", list2[1]);
        Assert.Equal("three", list2[2]);
    }

    /// <summary>
    /// Writes data with the JSON codec, then verifies DurableValue round-trips.
    /// </summary>
    [Fact]
    public async Task JsonCodec_DurableValue_WriteAndRecover()
    {
        var storage = CreateStorage();
        var jsonOptions = new System.Text.Json.JsonSerializerOptions();

        // Write phase
        var sut = CreateTestSystemWithJsonCodec(storage, jsonOptions);
        var value = new DurableValue<int>("val", sut.Manager, new OrleansBinaryValueEntryCodec<int>(new JsonLogDataCodec<int>(jsonOptions)));
        await sut.Lifecycle.OnStart();

        value.Value = 42;
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        // Recovery phase
        var sut2 = CreateTestSystemWithJsonCodec(storage, jsonOptions);
        var value2 = new DurableValue<int>("val", sut2.Manager, new OrleansBinaryValueEntryCodec<int>(new JsonLogDataCodec<int>(jsonOptions)));
        await sut2.Lifecycle.OnStart();

        Assert.Equal(42, value2.Value);
    }

    internal (IStateMachineManager Manager, IStateMachineStorage Storage, ILifecycleSubject Lifecycle) CreateTestSystemWithJsonCodec(IStateMachineStorage? storage = null, System.Text.Json.JsonSerializerOptions? jsonOptions = null)
    {
        storage ??= CreateStorage();
        jsonOptions ??= new System.Text.Json.JsonSerializerOptions();

        var stringCodec = new JsonLogDataCodec<string>(jsonOptions);
        var uint64Codec = new JsonLogDataCodec<ulong>(jsonOptions);
        var dateTimeCodec = new JsonLogDataCodec<DateTime>(jsonOptions);
        var stateMachineIdsCodec = new OrleansBinaryDictionaryEntryCodec<string, ulong>(stringCodec, uint64Codec);
        var retirementTrackerCodec = new OrleansBinaryDictionaryEntryCodec<string, DateTime>(stringCodec, dateTimeCodec);
        var manager = new StateMachineManager(storage, LoggerFactory.CreateLogger<StateMachineManager>(), Microsoft.Extensions.Options.Options.Create(ManagerOptions), stateMachineIdsCodec, retirementTrackerCodec, TimeProvider.System);
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
