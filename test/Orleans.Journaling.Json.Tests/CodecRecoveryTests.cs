using System.Buffers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Core;
using Orleans.Journaling.Json;
using Orleans.Journaling.Tests;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;
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
        var dict = new DurableDictionary<string, int>("dict", sut.Manager, new OrleansBinaryDictionaryOperationCodec<string, int>(keyCodec, valueCodec, SessionPool));
        await sut.Lifecycle.OnStart();

        dict.Add("alpha", 1);
        dict.Add("beta", 2);
        dict.Add("gamma", 3);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        // Recovery phase — new manager, same storage
        var sut2 = CreateTestSystem(storage);
        var keyCodec2 = new OrleansJournalValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool);
        var valueCodec2 = new OrleansJournalValueCodec<int>(CodecProvider.GetCodec<int>(), SessionPool);
        var dict2 = new DurableDictionary<string, int>("dict", sut2.Manager, new OrleansBinaryDictionaryOperationCodec<string, int>(keyCodec2, valueCodec2, SessionPool));
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
    public async Task Recovery_BinaryJournalWithJsonFormat_MigratesOnFirstWrite()
    {
        var storage = new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey);
        using var first = CreateFormatAwareTestSystem(storage, OrleansBinaryJournalFormat.JournalFormatKey);
        var dict = CreateFormatAwareDictionary(first, OrleansBinaryJournalFormat.JournalFormatKey);
        await first.Lifecycle.OnStart();
        dict.Add("alpha", 1);
        await first.Manager.WriteStateAsync(CancellationToken.None);
        Assert.Equal(OrleansBinaryJournalFormat.JournalFormatKey, storage.StoredJournalFormatKey);

        storage.SetConfiguredJournalFormatKey(JsonJournalExtensions.JournalFormatKey);
        using var recovered = CreateFormatAwareTestSystem(storage, JsonJournalExtensions.JournalFormatKey);
        var recoveredDict = CreateFormatAwareDictionary(recovered, JsonJournalExtensions.JournalFormatKey);
        await recovered.Lifecycle.OnStart();

        Assert.Equal(1, recoveredDict["alpha"]);

        recoveredDict.Add("beta", 2);
        await recovered.Manager.WriteStateAsync(CancellationToken.None);

        Assert.Equal(JsonJournalExtensions.JournalFormatKey, storage.StoredJournalFormatKey);
        Assert.Single(storage.Segments);
        var migratedJournal = Encoding.UTF8.GetString(storage.Segments.Single());
        Assert.Contains("\"alpha\"", migratedJournal, StringComparison.Ordinal);
        Assert.Contains("\"beta\"", migratedJournal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recovery_JsonJournalWithBinaryFormat_MigratesOnFirstWrite()
    {
        var storage = new VolatileJournalStorage(JsonJournalExtensions.JournalFormatKey);
        using var first = CreateFormatAwareTestSystem(storage, JsonJournalExtensions.JournalFormatKey);
        var dict = CreateFormatAwareDictionary(first, JsonJournalExtensions.JournalFormatKey);
        await first.Lifecycle.OnStart();
        dict.Add("alpha", 1);
        await first.Manager.WriteStateAsync(CancellationToken.None);
        Assert.Equal(JsonJournalExtensions.JournalFormatKey, storage.StoredJournalFormatKey);

        storage.SetConfiguredJournalFormatKey(OrleansBinaryJournalFormat.JournalFormatKey);
        using var recovered = CreateFormatAwareTestSystem(storage, OrleansBinaryJournalFormat.JournalFormatKey);
        var recoveredDict = CreateFormatAwareDictionary(recovered, OrleansBinaryJournalFormat.JournalFormatKey);
        await recovered.Lifecycle.OnStart();

        Assert.Equal(1, recoveredDict["alpha"]);

        recoveredDict.Add("beta", 2);
        await recovered.Manager.WriteStateAsync(CancellationToken.None);

        Assert.Equal(OrleansBinaryJournalFormat.JournalFormatKey, storage.StoredJournalFormatKey);
        Assert.Single(storage.Segments);

        using var final = CreateFormatAwareTestSystem(storage, OrleansBinaryJournalFormat.JournalFormatKey);
        var finalDict = CreateFormatAwareDictionary(final, OrleansBinaryJournalFormat.JournalFormatKey);
        await final.Lifecycle.OnStart();
        Assert.Equal(1, finalDict["alpha"]);
        Assert.Equal(2, finalDict["beta"]);
    }

    [Fact]
    public async Task Recovery_MetadataLessJournal_UsesLegacyFallbackAndMigratesOnFirstWrite()
    {
        var storage = new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey);
        using var first = CreateFormatAwareTestSystem(storage, OrleansBinaryJournalFormat.JournalFormatKey);
        var dict = CreateFormatAwareDictionary(first, OrleansBinaryJournalFormat.JournalFormatKey);
        await first.Lifecycle.OnStart();
        dict.Add("alpha", 1);
        await first.Manager.WriteStateAsync(CancellationToken.None);
        storage.StoredJournalFormatKey = null;

        storage.SetConfiguredJournalFormatKey(JsonJournalExtensions.JournalFormatKey);
        using var recovered = CreateFormatAwareTestSystem(
            storage,
            JsonJournalExtensions.JournalFormatKey,
            legacyJournalFormatKey: OrleansBinaryJournalFormat.JournalFormatKey);
        var recoveredDict = CreateFormatAwareDictionary(recovered, JsonJournalExtensions.JournalFormatKey);
        await recovered.Lifecycle.OnStart();

        Assert.Equal(1, recoveredDict["alpha"]);

        recoveredDict.Add("beta", 2);
        await recovered.Manager.WriteStateAsync(CancellationToken.None);

        Assert.Equal(JsonJournalExtensions.JournalFormatKey, storage.StoredJournalFormatKey);
        Assert.Single(storage.Segments);
    }

    [Fact]
    public async Task Recovery_EmptyJournalWithStaleMetadata_WritesConfiguredFormat()
    {
        var storage = new VolatileJournalStorage(JsonJournalExtensions.JournalFormatKey);
        storage.StoredJournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey;
        using var system = CreateFormatAwareTestSystem(
            storage,
            JsonJournalExtensions.JournalFormatKey,
            legacyJournalFormatKey: OrleansBinaryJournalFormat.JournalFormatKey);
        var dict = CreateFormatAwareDictionary(system, JsonJournalExtensions.JournalFormatKey);
        await system.Lifecycle.OnStart();

        dict.Add("alpha", 1);
        await system.Manager.WriteStateAsync(CancellationToken.None);

        Assert.Equal(JsonJournalExtensions.JournalFormatKey, storage.StoredJournalFormatKey);
        Assert.Contains("""[8,"set","alpha",1]""", Encoding.UTF8.GetString(storage.Segments.Single()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migration_WithUnregisteredRetiredState_ThrowsClearError()
    {
        var storage = new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey);
        using var first = CreateFormatAwareTestSystem(storage, OrleansBinaryJournalFormat.JournalFormatKey);
        var dict = CreateFormatAwareDictionary(first, OrleansBinaryJournalFormat.JournalFormatKey, "dict");
        await first.Lifecycle.OnStart();
        dict.Add("alpha", 1);
        await first.Manager.WriteStateAsync(CancellationToken.None);

        storage.SetConfiguredJournalFormatKey(JsonJournalExtensions.JournalFormatKey);
        using var recovered = CreateFormatAwareTestSystem(storage, JsonJournalExtensions.JournalFormatKey);
        var other = CreateFormatAwareDictionary(recovered, JsonJournalExtensions.JournalFormatKey, "other");
        await recovered.Lifecycle.OnStart();

        other.Add("beta", 2);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => recovered.Manager.WriteStateAsync(CancellationToken.None).AsTask());

        Assert.Contains("Cannot migrate journal", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not currently registered", exception.Message, StringComparison.Ordinal);
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

    private static VolatileJournalStorage CreateJsonStorage() => new(JsonJournalExtensions.JournalFormatKey);

    private static System.Text.Json.JsonSerializerOptions CreateJsonOptions()
        => new() { TypeInfoResolver = JsonCodecTestJsonContext.Default };

    private FormatAwareTestSystem CreateFormatAwareTestSystem(
        VolatileJournalStorage storage,
        string writeJournalFormatKey,
        string? legacyJournalFormatKey = null)
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddLogging();
        services.AddSingleton(typeof(IJournalValueCodec<>), typeof(OrleansJournalValueCodec<>));
        services.AddKeyedSingleton<IJournalFormat>(
            OrleansBinaryJournalFormat.JournalFormatKey,
            (sp, _) => new OrleansBinaryJournalFormat(sp.GetRequiredService<SerializerSessionPool>()));
        services.AddKeyedSingleton(
            typeof(IDurableDictionaryOperationCodec<,>),
            OrleansBinaryJournalFormat.JournalFormatKey,
            typeof(OrleansBinaryDictionaryOperationCodec<,>));

        var jsonOptions = CreateJsonOptions();
        services.AddSingleton(new JsonJournalOptions { SerializerOptions = jsonOptions });
        services.AddKeyedSingleton<IJournalFormat>(JsonJournalExtensions.JournalFormatKey, new JsonLinesJournalFormat());
        services.AddKeyedSingleton(
            typeof(IDurableDictionaryOperationCodec<,>),
            JsonJournalExtensions.JournalFormatKey,
            typeof(JsonDictionaryOperationCodecService<,>));

        var serviceProvider = services.BuildServiceProvider();
        var managerOptions = new StateManagerOptions();
        if (legacyJournalFormatKey is not null)
        {
            managerOptions.LegacyJournalFormatKey = legacyJournalFormatKey;
        }

        var manager = new JournaledStateManager(
            storage,
            serviceProvider.GetRequiredService<ILogger<JournaledStateManager>>(),
            Microsoft.Extensions.Options.Options.Create(managerOptions),
            TimeProvider.System,
            serviceProvider,
            writeJournalFormatKey);
        var lifecycle = new TestGrainLifecycle(serviceProvider.GetRequiredService<ILogger<TestGrainLifecycle>>());
        (manager as ILifecycleParticipant<IGrainLifecycle>)?.Participate(lifecycle);
        return new(serviceProvider, manager, lifecycle);
    }

    private static DurableDictionary<string, int> CreateFormatAwareDictionary(
        FormatAwareTestSystem system,
        string writeJournalFormatKey,
        string name = "dict")
        => new(name, system.Manager, writeJournalFormatKey, system.ServiceProvider);

    private OrleansBinaryDictionaryOperationCodec<TKey, TValue> CreateBinaryDictionaryCodec<TKey, TValue>()
        where TKey : notnull
        => new(ValueCodec<TKey>(), ValueCodec<TValue>(), SessionPool);

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

        Assert.Contains($"journal format key '{journalFormatKey}'", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.InnerException);
        return exception;
    }

    private DeepCopier<T> Copier<T>() => ServiceProvider.GetRequiredService<DeepCopier<T>>();

    private sealed class FormatAwareTestSystem(ServiceProvider serviceProvider, JournaledStateManager manager, TestGrainLifecycle lifecycle) : IDisposable
    {
        public ServiceProvider ServiceProvider { get; } = serviceProvider;

        public JournaledStateManager Manager { get; } = manager;

        public TestGrainLifecycle Lifecycle { get; } = lifecycle;

        public void Dispose()
        {
            ((IDisposable)Manager).Dispose();
            ServiceProvider.Dispose();
        }
    }

    private sealed class TestGrainLifecycle(ILogger logger) : LifecycleSubject(logger), IGrainLifecycle
    {
        public void AddMigrationParticipant(IGrainMigrationParticipant participant) { }
        public void RemoveMigrationParticipant(IGrainMigrationParticipant participant) { }
    }
}
