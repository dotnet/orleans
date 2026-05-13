using System.Buffers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Core;
using Orleans.Journaling.Json;
using Orleans.Journaling.Tests;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Codecs;
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
        var keyCodec = CodecProvider.GetCodec<string>();
        var valueCodec = CodecProvider.GetCodec<int>();
        var dict = new DurableDictionary<string, int>("dict", sut.Manager, new OrleansBinaryDurableDictionaryCommandCodec<string, int>(keyCodec, valueCodec, SessionPool));
        await sut.Lifecycle.OnStart();

        dict.Add("alpha", 1);
        dict.Add("beta", 2);
        dict.Add("gamma", 3);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        // Recovery phase — new manager, same storage
        var sut2 = CreateTestSystem(storage);
        var keyCodec2 = CodecProvider.GetCodec<string>();
        var valueCodec2 = CodecProvider.GetCodec<int>();
        var dict2 = new DurableDictionary<string, int>("dict", sut2.Manager, new OrleansBinaryDurableDictionaryCommandCodec<string, int>(keyCodec2, valueCodec2, SessionPool));
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
        var dict = new DurableDictionary<string, int>("dict", sut.Manager, new JsonDurableDictionaryCommandCodec<string, int>(jsonOptions));
        await sut.Lifecycle.OnStart();

        dict.Add("alpha", 1);
        dict.Add("beta", 2);
        await sut.Manager.WriteStateAsync(CancellationToken.None);
        var journal = Encoding.UTF8.GetString(storage.Segments.Single());
        Assert.Equal(
            """[0,["set","dict",8]]""" + "\n" +
            """[8,["set","alpha",1]]""" + "\n" +
            """[8,["set","beta",2]]""" + "\n",
            journal);

        // Recovery phase
        var sut2 = CreateTestSystemWithJsonCodec(storage, jsonOptions);
        var dict2 = new DurableDictionary<string, int>("dict", sut2.Manager, new JsonDurableDictionaryCommandCodec<string, int>(jsonOptions));
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
        var list = new DurableList<string>("list", sut.Manager, new JsonDurableListCommandCodec<string>(jsonOptions));
        await sut.Lifecycle.OnStart();

        list.Add("one");
        list.Add("two");
        list.Add("three");
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        // Recovery phase
        var sut2 = CreateTestSystemWithJsonCodec(storage, jsonOptions);
        var list2 = new DurableList<string>("list", sut2.Manager, new JsonDurableListCommandCodec<string>(jsonOptions));
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
        var value = new DurableValue<int>("val", sut.Manager, new JsonDurableValueCommandCodec<int>(jsonOptions));
        await sut.Lifecycle.OnStart();

        value.Value = 42;
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        // Recovery phase
        var sut2 = CreateTestSystemWithJsonCodec(storage, jsonOptions);
        var value2 = new DurableValue<int>("val", sut2.Manager, new JsonDurableValueCommandCodec<int>(jsonOptions));
        await sut2.Lifecycle.OnStart();

        Assert.Equal(42, value2.Value);
    }

    [Fact]
    public async Task JsonCodec_DurableQueueSetStateAndTcs_WriteAndRecover()
    {
        var storage = CreateJsonStorage();
        var jsonOptions = CreateJsonOptions();

        var sut = CreateTestSystemWithJsonCodec(storage, jsonOptions);
        var queue = new DurableQueue<string>("queue", sut.Manager, new JsonDurableQueueCommandCodec<string>(jsonOptions));
        var set = new DurableSet<string>("set", sut.Manager, new JsonDurableSetCommandCodec<string>(jsonOptions));
        var state = new DurableState<string>("state", sut.Manager, new JsonPersistentStateCommandCodec<string>(jsonOptions));
        var tcs = new DurableTaskCompletionSource<int>(
            "tcs",
            sut.Manager,
            new JsonDurableTaskCompletionSourceCommandCodec<int>(jsonOptions),
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
        var queue2 = new DurableQueue<string>("queue", sut2.Manager, new JsonDurableQueueCommandCodec<string>(jsonOptions));
        var set2 = new DurableSet<string>("set", sut2.Manager, new JsonDurableSetCommandCodec<string>(jsonOptions));
        var state2 = new DurableState<string>("state", sut2.Manager, new JsonPersistentStateCommandCodec<string>(jsonOptions));
        var tcs2 = new DurableTaskCompletionSource<int>(
            "tcs",
            sut2.Manager,
            new JsonDurableTaskCompletionSourceCommandCodec<int>(jsonOptions),
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
    public async Task Recovery_MetadataLessJournal_UsesConfiguredFormat()
    {
        var storage = new VolatileJournalStorage(JsonJournalExtensions.JournalFormatKey);
        using var first = CreateFormatAwareTestSystem(storage, JsonJournalExtensions.JournalFormatKey);
        var dict = CreateFormatAwareDictionary(first, JsonJournalExtensions.JournalFormatKey);
        await first.Lifecycle.OnStart();
        dict.Add("alpha", 1);
        await first.Manager.WriteStateAsync(CancellationToken.None);
        var metadataLessStorage = new MetadataOverridingStorage(storage, storedJournalFormatKey: null);

        using var recovered = CreateFormatAwareTestSystem(metadataLessStorage, JsonJournalExtensions.JournalFormatKey);
        var recoveredDict = CreateFormatAwareDictionary(recovered, JsonJournalExtensions.JournalFormatKey);
        await recovered.Lifecycle.OnStart();

        Assert.Equal(1, recoveredDict["alpha"]);

        recoveredDict.Add("beta", 2);
        await recovered.Manager.WriteStateAsync(CancellationToken.None);

        Assert.Equal(JsonJournalExtensions.JournalFormatKey, storage.StoredJournalFormatKey);
        Assert.Equal(2, storage.Segments.Count);
        Assert.Contains("""[8,["set","beta",2]]""", Encoding.UTF8.GetString(storage.Segments[^1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recovery_EmptyJournalWithStaleMetadata_WritesConfiguredFormat()
    {
        var storage = new VolatileJournalStorage(JsonJournalExtensions.JournalFormatKey);
        var staleMetadataStorage = new MetadataOverridingStorage(storage, OrleansBinaryJournalFormat.JournalFormatKey);
        using var system = CreateFormatAwareTestSystem(staleMetadataStorage, JsonJournalExtensions.JournalFormatKey);
        var dict = CreateFormatAwareDictionary(system, JsonJournalExtensions.JournalFormatKey);
        await system.Lifecycle.OnStart();

        dict.Add("alpha", 1);
        await system.Manager.WriteStateAsync(CancellationToken.None);

        Assert.Equal(JsonJournalExtensions.JournalFormatKey, storage.StoredJournalFormatKey);
        Assert.Contains("""[8,["set","alpha",1]]""", Encoding.UTF8.GetString(storage.Segments.Single()), StringComparison.Ordinal);
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
    [InlineData("[8,[\"set\",42]]\n[\"8\",[\"set\",43]]\n", "unsigned 32-bit integer stream id")]
    [InlineData("[8,[\"set\",42]]\n[9,[\"set\",43]]{}\n", "invalid JSON journal entry")]
    public async Task JsonRecovery_MalformedJournal_ThrowsFormatKeyError(string jsonLines, string expectedInnerMessage)
    {
        var storage = await CreateJsonStorageWithSegment(jsonLines);
        var sut = CreateTestSystemWithJsonCodec(storage);

        var exception = await AssertRecoveryFailsAsync(sut.Lifecycle, JsonJournalExtensions.JournalFormatKey);

        Assert.Contains(expectedInnerMessage, exception.InnerException!.Message, StringComparison.Ordinal);
    }

    internal (IJournaledStateManager Manager, IJournalStorage Storage, ILifecycleSubject Lifecycle) CreateTestSystemWithJsonCodec(IJournalStorage? storage = null, System.Text.Json.JsonSerializerOptions? jsonOptions = null)
    {
        storage ??= CreateJsonStorage();
        jsonOptions ??= CreateJsonOptions();

        var serviceProvider = CreateJsonServiceProvider(jsonOptions);
        var managerOptions = new JournaledStateManagerOptions
        {
            JournalFormatKey = JsonJournalExtensions.JournalFormatKey,
            RetirementGracePeriod = ManagerOptions.RetirementGracePeriod
        };
        if (storage is VolatileJournalStorage volatileStorage)
        {
            volatileStorage.SetConfiguredJournalFormatKey(managerOptions.JournalFormatKey);
        }

        var shared = new JournaledStateManagerShared(
            serviceProvider.GetRequiredService<ILogger<JournaledStateManager>>(),
            Microsoft.Extensions.Options.Options.Create(managerOptions),
            TimeProvider.System,
            storage,
            serviceProvider);
        var manager = new JournaledStateManager(shared);
        var lifecycle = new TestGrainLifecycle(serviceProvider.GetRequiredService<ILogger<TestGrainLifecycle>>());
        (manager as ILifecycleParticipant<IGrainLifecycle>)?.Participate(lifecycle);
        return (manager, storage, lifecycle);
    }

    private static VolatileJournalStorage CreateJsonStorage() => new(JsonJournalExtensions.JournalFormatKey);

    private static System.Text.Json.JsonSerializerOptions CreateJsonOptions()
        => new() { TypeInfoResolver = JsonCodecTestJsonContext.Default };

    private static ServiceProvider CreateJsonServiceProvider(System.Text.Json.JsonSerializerOptions jsonOptions)
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddLogging();
        services.AddSingleton(new JsonJournalOptions { SerializerOptions = jsonOptions });
        services.AddKeyedSingleton<IJournalFormat>(JsonJournalExtensions.JournalFormatKey, new JsonLinesJournalFormat());
        services.AddKeyedSingleton(
            typeof(IDurableDictionaryCommandCodec<,>),
            JsonJournalExtensions.JournalFormatKey,
            typeof(JsonDurableDictionaryCommandCodecService<,>));
        return services.BuildServiceProvider();
    }

    private FormatAwareTestSystem CreateFormatAwareTestSystem(
        IJournalStorage storage,
        string writeJournalFormatKey)
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddLogging();
        services.AddKeyedSingleton<IJournalFormat>(
            OrleansBinaryJournalFormat.JournalFormatKey,
            (sp, _) => new OrleansBinaryJournalFormat(sp.GetRequiredService<SerializerSessionPool>()));
        services.AddKeyedSingleton(
            typeof(IDurableDictionaryCommandCodec<,>),
            OrleansBinaryJournalFormat.JournalFormatKey,
            typeof(OrleansBinaryDurableDictionaryCommandCodec<,>));

        var jsonOptions = CreateJsonOptions();
        services.AddSingleton(new JsonJournalOptions { SerializerOptions = jsonOptions });
        services.AddKeyedSingleton<IJournalFormat>(JsonJournalExtensions.JournalFormatKey, new JsonLinesJournalFormat());
        services.AddKeyedSingleton(
            typeof(IDurableDictionaryCommandCodec<,>),
            JsonJournalExtensions.JournalFormatKey,
            typeof(JsonDurableDictionaryCommandCodecService<,>));

        var serviceProvider = services.BuildServiceProvider();
        var managerOptions = new JournaledStateManagerOptions
        {
            JournalFormatKey = writeJournalFormatKey
        };
        var shared = new JournaledStateManagerShared(
            serviceProvider.GetRequiredService<ILogger<JournaledStateManager>>(),
            Microsoft.Extensions.Options.Options.Create(managerOptions),
            TimeProvider.System,
            storage,
            serviceProvider);

        var manager = new JournaledStateManager(shared);
        var lifecycle = new TestGrainLifecycle(serviceProvider.GetRequiredService<ILogger<TestGrainLifecycle>>());
        (manager as ILifecycleParticipant<IGrainLifecycle>)?.Participate(lifecycle);
        return new(serviceProvider, manager, lifecycle);
    }

    private static DurableDictionary<string, int> CreateFormatAwareDictionary(
        FormatAwareTestSystem system,
        string writeJournalFormatKey,
        string name = "dict")
        => new(
            name,
            system.Manager,
            JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, int>>(
                system.ServiceProvider,
                writeJournalFormatKey));

    private OrleansBinaryDurableDictionaryCommandCodec<TKey, TValue> CreateBinaryDictionaryCodec<TKey, TValue>()
        where TKey : notnull
        => new(ValueCodec<TKey>(), ValueCodec<TValue>(), SessionPool);

    private IFieldCodec<T> ValueCodec<T>() => CodecProvider.GetCodec<T>();

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

    private sealed class MetadataOverridingStorage(VolatileJournalStorage inner, string? storedJournalFormatKey) : IJournalStorage
    {
        public bool IsCompactionRequested => inner.IsCompactionRequested;

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
            => inner.AppendAsync(value, cancellationToken);

        public ValueTask DeleteAsync(CancellationToken cancellationToken)
            => inner.DeleteAsync(cancellationToken);

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
            => inner.ReplaceAsync(value, cancellationToken);

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(consumer);

            var metadata = storedJournalFormatKey is null
                ? JournalFileMetadata.Empty
                : new JournalFileMetadata(storedJournalFormatKey);
            if (inner.Segments.Count == 0)
            {
                consumer.Complete(metadata);
            }
            else
            {
                consumer.Read(ReadSegments(cancellationToken), metadata, complete: true);
            }

            return default;
        }

        private IEnumerable<ReadOnlyMemory<byte>> ReadSegments(CancellationToken cancellationToken)
        {
            foreach (var segment in inner.Segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return segment;
            }
        }
    }

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
