using Microsoft.Extensions.Logging;
using Orleans.Journaling.Json;
using Orleans.Journaling.Tests;
using Orleans.Runtime;
using System.Text;
using Xunit;

namespace Orleans.Journaling.Json.Tests;

/// <summary>
/// Tests that verify same-format recovery for JSON journaling and the Orleans binary compatibility baseline.
/// </summary>
[TestCategory("BVT")]
public class CodecRecoveryTests : JournalingTestBase
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
        var keyCodec = new OrleansLogValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool);
        var valueCodec = new OrleansLogValueCodec<int>(CodecProvider.GetCodec<int>(), SessionPool);
        var dict = new DurableDictionary<string, int>("dict", sut.Manager, new OrleansBinaryDictionaryOperationCodec<string, int>(keyCodec, valueCodec));
        await sut.Lifecycle.OnStart();

        dict.Add("alpha", 1);
        dict.Add("beta", 2);
        dict.Add("gamma", 3);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        // Recovery phase — new manager, same storage
        var sut2 = CreateTestSystem(storage);
        var keyCodec2 = new OrleansLogValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool);
        var valueCodec2 = new OrleansLogValueCodec<int>(CodecProvider.GetCodec<int>(), SessionPool);
        var dict2 = new DurableDictionary<string, int>("dict", sut2.Manager, new OrleansBinaryDictionaryOperationCodec<string, int>(keyCodec2, valueCodec2));
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
        var storage = CreateJsonStorage();
        var jsonOptions = CreateJsonOptions();

        // Write phase
        var sut = CreateTestSystemWithJsonCodec(storage, jsonOptions);
        var dict = new DurableDictionary<string, int>("dict", sut.Manager, new JsonDictionaryOperationCodec<string, int>(jsonOptions));
        await sut.Lifecycle.OnStart();

        dict.Add("alpha", 1);
        dict.Add("beta", 2);
        await sut.Manager.WriteStateAsync(CancellationToken.None);
        var log = Encoding.UTF8.GetString(storage.Segments.Single());
        Assert.Equal(
            """{"streamId":0,"entry":{"cmd":"set","key":"dict","value":8}}""" + "\n" +
            """{"streamId":8,"entry":{"cmd":"set","key":"alpha","value":1}}""" + "\n" +
            """{"streamId":8,"entry":{"cmd":"set","key":"beta","value":2}}""" + "\n",
            log);

        // Recovery phase
        var sut2 = CreateTestSystemWithJsonCodec(storage, jsonOptions);
        var dict2 = new DurableDictionary<string, int>("dict", sut2.Manager, new JsonDictionaryOperationCodec<string, int>(jsonOptions));
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
        var storage = CreateJsonStorage();
        var jsonOptions = CreateJsonOptions();

        // Write phase
        var sut = CreateTestSystemWithJsonCodec(storage, jsonOptions);
        var list = new DurableList<string>("list", sut.Manager, new JsonListOperationCodec<string>(jsonOptions));
        await sut.Lifecycle.OnStart();

        list.Add("one");
        list.Add("two");
        list.Add("three");
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        // Recovery phase
        var sut2 = CreateTestSystemWithJsonCodec(storage, jsonOptions);
        var list2 = new DurableList<string>("list", sut2.Manager, new JsonListOperationCodec<string>(jsonOptions));
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
        var storage = CreateJsonStorage();
        var jsonOptions = CreateJsonOptions();

        // Write phase
        var sut = CreateTestSystemWithJsonCodec(storage, jsonOptions);
        var value = new DurableValue<int>("val", sut.Manager, new JsonValueOperationCodec<int>(jsonOptions));
        await sut.Lifecycle.OnStart();

        value.Value = 42;
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        // Recovery phase
        var sut2 = CreateTestSystemWithJsonCodec(storage, jsonOptions);
        var value2 = new DurableValue<int>("val", sut2.Manager, new JsonValueOperationCodec<int>(jsonOptions));
        await sut2.Lifecycle.OnStart();

        Assert.Equal(42, value2.Value);
    }

    internal (ILogManager Manager, ILogStorage Storage, ILifecycleSubject Lifecycle) CreateTestSystemWithJsonCodec(ILogStorage? storage = null, System.Text.Json.JsonSerializerOptions? jsonOptions = null)
    {
        storage ??= CreateJsonStorage();
        jsonOptions ??= CreateJsonOptions();

        var logStreamIdsCodec = new JsonDictionaryOperationCodec<string, ulong>(jsonOptions);
        var retirementTrackerCodec = new JsonDictionaryOperationCodec<string, DateTime>(jsonOptions);
        var manager = new LogManager(storage, LoggerFactory.CreateLogger<LogManager>(), Microsoft.Extensions.Options.Options.Create(ManagerOptions), logStreamIdsCodec, retirementTrackerCodec, TimeProvider.System, new JsonLinesLogFormat());
        var lifecycle = new TestGrainLifecycle(LoggerFactory.CreateLogger<TestGrainLifecycle>());
        (manager as ILifecycleParticipant<IGrainLifecycle>)?.Participate(lifecycle);
        return (manager, storage, lifecycle);
    }

    private static VolatileLogStorage CreateJsonStorage() => new();

    private static System.Text.Json.JsonSerializerOptions CreateJsonOptions()
        => new() { TypeInfoResolver = JsonCodecTestJsonContext.Default };

    private sealed class TestGrainLifecycle(ILogger logger) : LifecycleSubject(logger), IGrainLifecycle
    {
        public void AddMigrationParticipant(IGrainMigrationParticipant participant) { }
        public void RemoveMigrationParticipant(IGrainMigrationParticipant participant) { }
    }
}
