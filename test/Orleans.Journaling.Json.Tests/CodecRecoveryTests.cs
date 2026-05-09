using System.Buffers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Core;
using Orleans.Journaling.Json;
using Orleans.Journaling.Tests;
using Orleans.Runtime;
using Orleans.Serialization;
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
        var keyCodec = new OrleansJournalValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool);
        var valueCodec = new OrleansJournalValueCodec<int>(CodecProvider.GetCodec<int>(), SessionPool);
        var dict = new DurableDictionary<string, int>("dict", sut.Manager, new OrleansBinaryDictionaryOperationCodec<string, int>(keyCodec, valueCodec));
        await sut.Lifecycle.OnStart();

        dict.Add("alpha", 1);
        dict.Add("beta", 2);
        dict.Add("gamma", 3);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        // Recovery phase — new manager, same storage
        var sut2 = CreateTestSystem(storage);
        var keyCodec2 = new OrleansJournalValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool);
        var valueCodec2 = new OrleansJournalValueCodec<int>(CodecProvider.GetCodec<int>(), SessionPool);
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
        var journal = Encoding.UTF8.GetString(storage.Segments.Single());
        Assert.Equal(
            """[0,"set","dict",8]""" + "\n" +
            """[8,"set","alpha",1]""" + "\n" +
            """[8,"set","beta",2]""" + "\n",
            journal);

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

    [Fact]
    public async Task JsonCodec_DurableQueueSetStateAndTcs_WriteAndRecover()
    {
        var storage = CreateJsonStorage();
        var jsonOptions = CreateJsonOptions();

        var sut = CreateTestSystemWithJsonCodec(storage, jsonOptions);
        var queue = new DurableQueue<string>("queue", sut.Manager, new JsonQueueOperationCodec<string>(jsonOptions));
        var set = new DurableSet<string>("set", sut.Manager, new JsonSetOperationCodec<string>(jsonOptions));
        var state = new DurableState<string>("state", sut.Manager, new JsonStateOperationCodec<string>(jsonOptions));
        var tcs = new DurableTaskCompletionSource<int>(
            "tcs",
            sut.Manager,
            new JsonTcsOperationCodec<int>(jsonOptions),
            Copier<int>(),
            Copier<Exception>());
        await sut.Lifecycle.OnStart();

        queue.Enqueue("first");
        queue.Enqueue("second");
        set.Add("a");
        set.Add("b");
        ((IStorage<string>)state).State = "state-value";
        Assert.True(tcs.TrySetResult(17));
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var sut2 = CreateTestSystemWithJsonCodec(storage, jsonOptions);
        var queue2 = new DurableQueue<string>("queue", sut2.Manager, new JsonQueueOperationCodec<string>(jsonOptions));
        var set2 = new DurableSet<string>("set", sut2.Manager, new JsonSetOperationCodec<string>(jsonOptions));
        var state2 = new DurableState<string>("state", sut2.Manager, new JsonStateOperationCodec<string>(jsonOptions));
        var tcs2 = new DurableTaskCompletionSource<int>(
            "tcs",
            sut2.Manager,
            new JsonTcsOperationCodec<int>(jsonOptions),
            Copier<int>(),
            Copier<Exception>());
        await sut2.Lifecycle.OnStart();

        Assert.Equal(2, queue2.Count);
        Assert.Equal("first", queue2.Dequeue());
        Assert.Equal("second", queue2.Dequeue());
        Assert.True(set2.SetEquals(["a", "b"]));
        Assert.Equal("state-value", ((IStorage<string>)state2).State);
        Assert.Equal(DurableTaskCompletionSourceStatus.Completed, tcs2.State.Status);
        Assert.Equal(17, tcs2.State.Value);
        Assert.Equal(17, await tcs2.Task);
    }

    [Fact]
    public async Task Recovery_BinaryJournalWithJsonFormat_ThrowsFormatKeyError()
    {
        var storage = new VolatileJournalStorage();
        var sut = CreateTestSystem(storage);
        var dict = new DurableDictionary<string, int>("dict", sut.Manager, CreateBinaryDictionaryCodec<string, int>());
        await sut.Lifecycle.OnStart();
        dict.Add("alpha", 1);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var recovered = CreateTestSystemWithJsonCodec(storage);
        _ = new DurableDictionary<string, int>("dict", recovered.Manager, new JsonDictionaryOperationCodec<string, int>(CreateJsonOptions()));

        _ = await AssertRecoveryFailsAsync(recovered.Lifecycle, JsonJournalExtensions.JournalFormatKey);
    }

    [Fact]
    public async Task Recovery_JsonJournalWithBinaryFormat_ThrowsFormatKeyError()
    {
        var storage = CreateJsonStorage();
        var jsonOptions = CreateJsonOptions();
        var sut = CreateTestSystemWithJsonCodec(storage, jsonOptions);
        var dict = new DurableDictionary<string, int>("dict", sut.Manager, new JsonDictionaryOperationCodec<string, int>(jsonOptions));
        await sut.Lifecycle.OnStart();
        dict.Add("alpha", 1);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var recovered = CreateTestSystem(storage);
        _ = new DurableDictionary<string, int>("dict", recovered.Manager, CreateBinaryDictionaryCodec<string, int>());

        _ = await AssertRecoveryFailsAsync(recovered.Lifecycle, OrleansBinaryJournalFormat.JournalFormatKey);
    }

    [Theory]
    [InlineData("[8,\"set\",42]\n[\"8\",\"set\",43]\n", "unsigned integer stream id")]
    [InlineData("[8,\"set\",42]\n[9,\"set\",43]{}\n", "invalid JSON journal entry")]
    public async Task JsonRecovery_MalformedJournal_ThrowsFormatKeyError(string jsonLines, string expectedInnerMessage)
    {
        var storage = await CreateJsonStorageWithSegment(jsonLines);
        var sut = CreateTestSystemWithJsonCodec(storage);

        var exception = await AssertRecoveryFailsAsync(sut.Lifecycle, JsonJournalExtensions.JournalFormatKey);

        Assert.Contains(expectedInnerMessage, exception.InnerException!.Message, StringComparison.Ordinal);
    }

    internal (IStateManager Manager, IJournalStorage Storage, ILifecycleSubject Lifecycle) CreateTestSystemWithJsonCodec(IJournalStorage? storage = null, System.Text.Json.JsonSerializerOptions? jsonOptions = null)
    {
        storage ??= CreateJsonStorage();
        jsonOptions ??= CreateJsonOptions();

        var journalStreamIdsCodec = new JsonDictionaryOperationCodec<string, ulong>(jsonOptions);
        var retirementTrackerCodec = new JsonDictionaryOperationCodec<string, DateTime>(jsonOptions);
        var manager = new JournaledStateManager(
            storage,
            LoggerFactory.CreateLogger<JournaledStateManager>(),
            Microsoft.Extensions.Options.Options.Create(ManagerOptions),
            journalStreamIdsCodec,
            retirementTrackerCodec,
            TimeProvider.System,
            new JsonLinesJournalFormat(),
            JsonJournalExtensions.JournalFormatKey);
        var lifecycle = new TestGrainLifecycle(LoggerFactory.CreateLogger<TestGrainLifecycle>());
        (manager as ILifecycleParticipant<IGrainLifecycle>)?.Participate(lifecycle);
        return (manager, storage, lifecycle);
    }

    private static VolatileJournalStorage CreateJsonStorage() => new();

    private static System.Text.Json.JsonSerializerOptions CreateJsonOptions()
        => new() { TypeInfoResolver = JsonCodecTestJsonContext.Default };

    private OrleansBinaryDictionaryOperationCodec<TKey, TValue> CreateBinaryDictionaryCodec<TKey, TValue>()
        where TKey : notnull
        => new(ValueCodec<TKey>(), ValueCodec<TValue>());

    private IJournalValueCodec<T> ValueCodec<T>() => new OrleansJournalValueCodec<T>(CodecProvider.GetCodec<T>(), SessionPool);

    private static async Task<VolatileJournalStorage> CreateJsonStorageWithSegment(string jsonLines)
    {
        var storage = CreateJsonStorage();
        await storage.AppendAsync(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(jsonLines)), CancellationToken.None);
        return storage;
    }

    private static async Task<InvalidOperationException> AssertRecoveryFailsAsync(ILifecycleSubject lifecycle, string journalFormatKey)
    {
        InvalidOperationException exception;
        try
        {
            exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => lifecycle.OnStart().WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await lifecycle.OnStop(CancellationToken.None);
        }

        Assert.Contains($"configured journal format key '{journalFormatKey}'", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.InnerException);
        return exception;
    }

    private DeepCopier<T> Copier<T>() => ServiceProvider.GetRequiredService<DeepCopier<T>>();

    private sealed class TestGrainLifecycle(ILogger logger) : LifecycleSubject(logger), IGrainLifecycle
    {
        public void AddMigrationParticipant(IGrainMigrationParticipant participant) { }
        public void RemoveMigrationParticipant(IGrainMigrationParticipant participant) { }
    }
}
