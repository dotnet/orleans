using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Internal;
using Orleans.Core;
using Orleans.Journaling.Json;
using Orleans.Serialization;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("AzureStorage"), TestCategory("Functional")]
public sealed class AzureTableCodecRecoveryTests : JournalingTestBase, IAsyncLifetime
{
    private ServiceProvider _azureServiceProvider = null!;
    private SiloLifecycleSubject _siloLifecycle = null!;
    private AzureTableJournalStorageProvider _storageProvider = null!;

    public AzureTableCodecRecoveryTests()
    {
        JournalingAzureStorageTestConfiguration.CheckPreconditionsOrThrow();
    }

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AzureTableJournalStorageOptions>(options => JournalingAzureStorageTestConfiguration.ConfigureTestDefaults(options));
        services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey);
        services.AddSerializer();
        ConfigureFormatServices(services);
        services.AddSingleton<AzureTableJournalStorageProvider>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, AzureTableJournalStorageProvider>();

        _azureServiceProvider = services.BuildServiceProvider();
        _storageProvider = _azureServiceProvider.GetRequiredService<AzureTableJournalStorageProvider>();
        _siloLifecycle = new SiloLifecycleSubject(_azureServiceProvider.GetRequiredService<ILogger<SiloLifecycleSubject>>());

        foreach (var participant in _azureServiceProvider.GetServices<ILifecycleParticipant<ISiloLifecycle>>())
        {
            participant.Participate(_siloLifecycle);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await _siloLifecycle.OnStart(cts.Token);
    }

    public async Task DisposeAsync()
    {
        if (_siloLifecycle is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await _siloLifecycle.OnStop(cts.Token);
        }

        await _azureServiceProvider.DisposeAsync();
    }

    [SkippableFact]
    public async Task AzureTableStorage_BinaryJournal_MigratesToJsonOnFirstWrite()
    {
        var grainId = GrainId.Create("journaling-table-codec-migration", Guid.NewGuid().ToString("N"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await using (var binaryProvider = await CreateAzureProviderAsync(OrleansBinaryJournalFormat.JournalFormatKey, cts.Token))
        {
            var storage = binaryProvider.StorageProvider.CreateStorage(JournalId.FromGrainId(grainId));
            var manager = CreateFormatAwareManager(binaryProvider.ServiceProvider, storage, OrleansBinaryJournalFormat.JournalFormatKey);
            var dict = CreateFormatAwareDictionary(binaryProvider.ServiceProvider, manager, OrleansBinaryJournalFormat.JournalFormatKey);
            await manager.InitializeAsync(cts.Token);

            dict.Add("alpha", 1);
            await manager.WriteStateAsync(cts.Token);
            ((IDisposable)manager).Dispose();
        }

        await using var jsonProvider = await CreateAzureProviderAsync(JsonJournalExtensions.JournalFormatKey, cts.Token);
        var migratedStorage = jsonProvider.StorageProvider.CreateStorage(JournalId.FromGrainId(grainId));
        var migratedManager = CreateFormatAwareManager(jsonProvider.ServiceProvider, migratedStorage, JsonJournalExtensions.JournalFormatKey);
        var migratedDict = CreateFormatAwareDictionary(jsonProvider.ServiceProvider, migratedManager, JsonJournalExtensions.JournalFormatKey);
        await migratedManager.InitializeAsync(cts.Token);

        Assert.Equal(1, migratedDict["alpha"]);

        migratedDict.Add("beta", 2);
        await migratedManager.WriteStateAsync(cts.Token);
        ((IDisposable)migratedManager).Dispose();

        var recoveredStorage = jsonProvider.StorageProvider.CreateStorage(JournalId.FromGrainId(grainId));
        var recoveredManager = CreateFormatAwareManager(jsonProvider.ServiceProvider, recoveredStorage, JsonJournalExtensions.JournalFormatKey);
        var recoveredDict = CreateFormatAwareDictionary(jsonProvider.ServiceProvider, recoveredManager, JsonJournalExtensions.JournalFormatKey);
        await recoveredManager.InitializeAsync(cts.Token);

        Assert.Equal(1, recoveredDict["alpha"]);
        Assert.Equal(2, recoveredDict["beta"]);
        await recoveredStorage.DeleteAsync(cts.Token);
        ((IDisposable)recoveredManager).Dispose();
    }

    [SkippableFact]
    public async Task AzureTableStorage_AllDurableTypes_RecoverWithBinaryCodec()
    {
        var grainId = GrainId.Create("journaling-table-codec-recovery", Guid.NewGuid().ToString("N"));
        var storage = _storageProvider.CreateStorage(JournalId.FromGrainId(grainId));
        var first = CreateStates(storage);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await first.Manager.InitializeAsync(cts.Token);

        first.Dictionary.Add("alpha", 1);
        first.Dictionary.Add("beta", 2);
        first.List.Add("one");
        first.List.Add("two");
        first.Queue.Enqueue("first");
        first.Queue.Enqueue("second");
        first.Set.Add("a");
        first.Set.Add("b");
        first.Value.Value = 42;
        ((IStorage<string>)first.State).State = "state-value";
        Assert.True(first.Tcs.TrySetResult(17));
        await first.Manager.WriteStateAsync(cts.Token);

        var recoveredStorage = _storageProvider.CreateStorage(JournalId.FromGrainId(grainId));
        var recovered = CreateStates(recoveredStorage);
        await recovered.Manager.InitializeAsync(cts.Token);

        Assert.Equal(2, recovered.Dictionary.Count);
        Assert.Equal(1, recovered.Dictionary["alpha"]);
        Assert.Equal(2, recovered.Dictionary["beta"]);
        Assert.Equal(["one", "two"], recovered.List);
        Assert.Equal(2, recovered.Queue.Count);
        Assert.Equal("first", recovered.Queue.Dequeue());
        Assert.Equal("second", recovered.Queue.Dequeue());
        Assert.True(recovered.Set.SetEquals(["a", "b"]));
        Assert.Equal(42, recovered.Value.Value);
        Assert.Equal("state-value", ((IStorage<string>)recovered.State).State);
        Assert.Equal(DurableTaskCompletionSourceStatus.Completed, recovered.Tcs.State.Status);
        Assert.Equal(17, recovered.Tcs.State.Value);
        Assert.Equal(17, await recovered.Tcs.Task);

        await recoveredStorage.DeleteAsync(cts.Token);
    }

    [SkippableFact]
    public async Task AzureTableStorage_ReplaceAndAppend_RecoverAcrossFreshProviderInstances()
    {
        var grainId = GrainId.Create("journaling-table-replace-recovery", Guid.NewGuid().ToString("N"));
        var replacedBytes = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var appendedBytes = new byte[] { 0x50, 0x60, 0x70 };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await using (var writerProvider = await CreateAzureProviderAsync(OrleansBinaryJournalFormat.JournalFormatKey, cts.Token))
        {
            var storage = writerProvider.StorageProvider.CreateStorage(JournalId.FromGrainId(grainId));
            await storage.ReplaceAsync(new ReadOnlySequence<byte>(replacedBytes), cts.Token);
            await storage.AppendAsync(new ReadOnlySequence<byte>(appendedBytes), cts.Token);
        }

        await using var readerProvider = await CreateAzureProviderAsync(OrleansBinaryJournalFormat.JournalFormatKey, cts.Token);
        var recoveredStorage = readerProvider.StorageProvider.CreateStorage(JournalId.FromGrainId(grainId));
        var recovered = new RecordingJournalStorageConsumer();

        await recoveredStorage.ReadAsync(recovered, cts.Token);

        Assert.True(recovered.IsCompleted);
        Assert.NotEmpty(recovered.Formats);
        Assert.All(recovered.Formats, format => Assert.Equal(OrleansBinaryJournalFormat.JournalFormatKey, format));
        Assert.Equal([.. replacedBytes, .. appendedBytes], recovered.Bytes.ToArray());

        await recoveredStorage.DeleteAsync(cts.Token);
    }

    [SkippableFact]
    public async Task AzureTableStorage_LargeAppend_RoundTripsAcrossPropertiesAndRows()
    {
        var grainId = GrainId.Create("journaling-table-large-append", Guid.NewGuid().ToString("N"));
        var storage = _storageProvider.CreateStorage(JournalId.FromGrainId(grainId));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Larger than one data row (15 x 64 KiB) so the append spans multiple rows in one transaction.
        var payload = new byte[1024 * 1024 + 3];
        Random.Shared.NextBytes(payload);
        await storage.AppendAsync(new ReadOnlySequence<byte>(payload), cts.Token);

        var recoveredStorage = _storageProvider.CreateStorage(JournalId.FromGrainId(grainId));
        var recovered = new RecordingJournalStorageConsumer();
        await recoveredStorage.ReadAsync(recovered, cts.Token);

        Assert.True(recovered.IsCompleted);
        Assert.NotEmpty(recovered.Formats);
        Assert.All(recovered.Formats, format => Assert.Equal(OrleansBinaryJournalFormat.JournalFormatKey, format));
        Assert.Equal(payload, recovered.Bytes.ToArray());
        var metadata = Assert.IsAssignableFrom<IJournalMetadata>(await recoveredStorage.GetMetadataAsync(cts.Token));
        Assert.Equal(OrleansBinaryJournalFormat.JournalFormatKey, metadata.Format);
        Assert.NotNull(metadata.ETag);

        await recoveredStorage.DeleteAsync(cts.Token);
    }

    private DurableStates CreateStates(IJournalStorage storage)
    {
        var manager = CreateManager(storage);
        return new DurableStates(
            manager,
            new DurableDictionary<string, int>("dict", manager, new OrleansBinaryDurableDictionaryCommandCodec<string, int>(ValueCodec<string>(), ValueCodec<int>(), SessionPool)),
            new DurableList<string>("list", manager, new OrleansBinaryDurableListCommandCodec<string>(ValueCodec<string>(), SessionPool)),
            new DurableQueue<string>("queue", manager, new OrleansBinaryDurableQueueCommandCodec<string>(ValueCodec<string>(), SessionPool)),
            new DurableSet<string>("set", manager, new OrleansBinaryDurableSetCommandCodec<string>(ValueCodec<string>(), SessionPool)),
            new DurableValue<int>("value", manager, new OrleansBinaryDurableValueCommandCodec<int>(ValueCodec<int>(), SessionPool)),
            new DurableState<string>("state", manager, new OrleansBinaryPersistentStateCommandCodec<string>(ValueCodec<string>(), SessionPool)),
            new DurableTaskCompletionSource<int>(
                "tcs",
                manager,
                new OrleansBinaryDurableTaskCompletionSourceCommandCodec<int>(ValueCodec<int>(), ValueCodec<Exception>(), SessionPool),
                Copier<int>(),
                Copier<Exception>()));
    }

    private JournaledStateManager CreateManager(IJournalStorage storage)
    {
        var shared = new JournaledStateManagerShared(
            LoggerFactory.CreateLogger<JournaledStateManager>(),
            Options.Create(ManagerOptions),
            TimeProvider.System,
            ServiceProvider);

        return new(shared, storage);
    }

    private IFieldCodec<T> ValueCodec<T>() => CodecProvider.GetCodec<T>();

    private DeepCopier<T> Copier<T>() => ServiceProvider.GetRequiredService<DeepCopier<T>>();

    private static void ConfigureFormatServices(IServiceCollection services)
    {
        services.AddKeyedSingleton<IJournalFormat>(
            OrleansBinaryJournalFormat.JournalFormatKey,
            (sp, _) => new OrleansBinaryJournalFormat(sp.GetRequiredService<SerializerSessionPool>()));
        services.AddKeyedSingleton(
            typeof(IDurableDictionaryCommandCodec<,>),
            OrleansBinaryJournalFormat.JournalFormatKey,
            typeof(OrleansBinaryDurableDictionaryCommandCodec<,>));

        var jsonOptions = new System.Text.Json.JsonSerializerOptions { TypeInfoResolver = JournalingTestsJsonContext.Default };
        services.Configure<JsonJournalOptions>(options => options.SerializerOptions = jsonOptions);
        services.AddKeyedSingleton<IJournalFormat>(JsonJournalExtensions.JournalFormatKey, new JsonLinesJournalFormat());
        services.AddKeyedSingleton(
            typeof(IDurableDictionaryCommandCodec<,>),
            JsonJournalExtensions.JournalFormatKey,
            typeof(JsonDurableDictionaryCommandCodecService<,>));
    }

    private async Task<AzureProviderFixture> CreateAzureProviderAsync(string journalFormatKey, CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AzureTableJournalStorageOptions>(options => JournalingAzureStorageTestConfiguration.ConfigureTestDefaults(options));
        services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = journalFormatKey);
        services.AddSerializer();
        ConfigureFormatServices(services);
        services.AddSingleton<AzureTableJournalStorageProvider>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, AzureTableJournalStorageProvider>();

        var serviceProvider = services.BuildServiceProvider();
        var lifecycle = new SiloLifecycleSubject(serviceProvider.GetRequiredService<ILogger<SiloLifecycleSubject>>());
        foreach (var participant in serviceProvider.GetServices<ILifecycleParticipant<ISiloLifecycle>>())
        {
            participant.Participate(lifecycle);
        }

        await lifecycle.OnStart(cancellationToken);
        return new(serviceProvider, lifecycle, serviceProvider.GetRequiredService<AzureTableJournalStorageProvider>());
    }

    private static JournaledStateManager CreateFormatAwareManager(IServiceProvider serviceProvider, IJournalStorage storage, string journalFormatKey)
    {
        var shared = new JournaledStateManagerShared(
            serviceProvider.GetRequiredService<ILogger<JournaledStateManager>>(),
            Options.Create(new JournaledStateManagerOptions { JournalFormatKey = journalFormatKey }),
            TimeProvider.System,
            serviceProvider);

        return new(shared, storage);
    }

    private static DurableDictionary<string, int> CreateFormatAwareDictionary(IServiceProvider serviceProvider, JournaledStateManager manager, string journalFormatKey)
        => new(
            "dict",
            manager,
            JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, int>>(
                serviceProvider,
                journalFormatKey));

    private sealed class AzureProviderFixture(
        ServiceProvider serviceProvider,
        SiloLifecycleSubject lifecycle,
        AzureTableJournalStorageProvider storageProvider) : IAsyncDisposable
    {
        public ServiceProvider ServiceProvider { get; } = serviceProvider;

        public AzureTableJournalStorageProvider StorageProvider { get; } = storageProvider;

        public async ValueTask DisposeAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await lifecycle.OnStop(cts.Token);
            await ServiceProvider.DisposeAsync();
        }
    }

    private sealed class RecordingJournalStorageConsumer : IJournalStorageConsumer
    {
        public List<byte> Bytes { get; } = [];

        public List<string?> Formats { get; } = [];

        public bool IsCompleted { get; private set; }

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            if (buffer.Length > 0)
            {
                Bytes.AddRange(buffer.ToArray());
                buffer.Skip(buffer.Length);
                Formats.Add(metadata?.Format);
            }

            IsCompleted |= buffer.IsCompleted;
        }
    }

    private sealed record DurableStates(
        JournaledStateManager Manager,
        DurableDictionary<string, int> Dictionary,
        DurableList<string> List,
        DurableQueue<string> Queue,
        DurableSet<string> Set,
        DurableValue<int> Value,
        DurableState<string> State,
        DurableTaskCompletionSource<int> Tcs);

    [SkippableTheory]
    [InlineData(OrleansBinaryJournalFormat.JournalFormatKey)]
    [InlineData(JsonJournalExtensions.JournalFormatKey)]
    public async Task AzureTableStorage_JournalCodec_RecoversAcrossFreshProviderInstances(string journalFormatKey)
    {
        var grainId = GrainId.Create("journaling-table-fresh-provider", Guid.NewGuid().ToString("N"));
        var journalId = JournalId.FromGrainId(grainId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await using (var writerProvider = await CreateAzureProviderAsync(journalFormatKey, cts.Token))
        {
            var storage = writerProvider.StorageProvider.CreateStorage(journalId);
            var manager = CreateFormatAwareManager(writerProvider.ServiceProvider, storage, journalFormatKey);
            var dictionary = CreateFormatAwareDictionary(writerProvider.ServiceProvider, manager, journalFormatKey);
            await manager.InitializeAsync(cts.Token);

            dictionary.Add("alpha", 1);
            dictionary.Add("quoted-\"β\"", 42);
            await manager.WriteStateAsync(cts.Token);

            var metadata = Assert.IsAssignableFrom<IJournalMetadata>(await storage.GetMetadataAsync(cts.Token));
            Assert.Equal(journalFormatKey, metadata.Format);
            Assert.NotNull(metadata.ETag);
            ((IDisposable)manager).Dispose();
        }

        await using var readerProvider = await CreateAzureProviderAsync(journalFormatKey, cts.Token);
        var recoveredStorage = readerProvider.StorageProvider.CreateStorage(journalId);
        var recoveredManager = CreateFormatAwareManager(readerProvider.ServiceProvider, recoveredStorage, journalFormatKey);
        var recoveredDictionary = CreateFormatAwareDictionary(readerProvider.ServiceProvider, recoveredManager, journalFormatKey);
        await recoveredManager.InitializeAsync(cts.Token);

        Assert.Equal(2, recoveredDictionary.Count);
        Assert.Equal(1, recoveredDictionary["alpha"]);
        Assert.Equal(42, recoveredDictionary["quoted-\"β\""]);
        Assert.Equal(journalFormatKey, (await recoveredStorage.GetMetadataAsync(cts.Token))?.Format);

        await recoveredStorage.DeleteAsync(cts.Token);
        ((IDisposable)recoveredManager).Dispose();
    }

    [SkippableFact]
    public async Task AzureTableStorage_JsonJournal_MigratesToBinaryOnFirstWrite()
    {
        var grainId = GrainId.Create("journaling-table-json-to-binary", Guid.NewGuid().ToString("N"));
        var journalId = JournalId.FromGrainId(grainId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await using (var jsonProvider = await CreateAzureProviderAsync(JsonJournalExtensions.JournalFormatKey, cts.Token))
        {
            var storage = jsonProvider.StorageProvider.CreateStorage(journalId);
            var manager = CreateFormatAwareManager(jsonProvider.ServiceProvider, storage, JsonJournalExtensions.JournalFormatKey);
            var dictionary = CreateFormatAwareDictionary(jsonProvider.ServiceProvider, manager, JsonJournalExtensions.JournalFormatKey);
            await manager.InitializeAsync(cts.Token);
            dictionary.Add("alpha", 1);
            await manager.WriteStateAsync(cts.Token);
            ((IDisposable)manager).Dispose();
        }

        await using (var binaryProvider = await CreateAzureProviderAsync(OrleansBinaryJournalFormat.JournalFormatKey, cts.Token))
        {
            var storage = binaryProvider.StorageProvider.CreateStorage(journalId);
            var beforeRecovery = Assert.IsAssignableFrom<IJournalMetadata>(await storage.GetMetadataAsync(cts.Token));
            var manager = CreateFormatAwareManager(binaryProvider.ServiceProvider, storage, OrleansBinaryJournalFormat.JournalFormatKey);
            var dictionary = CreateFormatAwareDictionary(binaryProvider.ServiceProvider, manager, OrleansBinaryJournalFormat.JournalFormatKey);
            await manager.InitializeAsync(cts.Token);

            Assert.Equal(1, dictionary["alpha"]);
            var afterRecovery = Assert.IsAssignableFrom<IJournalMetadata>(await storage.GetMetadataAsync(cts.Token));
            Assert.Equal(JsonJournalExtensions.JournalFormatKey, afterRecovery.Format);
            Assert.Equal(beforeRecovery.ETag, afterRecovery.ETag);

            dictionary.Add("beta", 2);
            await manager.WriteStateAsync(cts.Token);

            var afterMigration = Assert.IsAssignableFrom<IJournalMetadata>(await storage.GetMetadataAsync(cts.Token));
            Assert.Equal(OrleansBinaryJournalFormat.JournalFormatKey, afterMigration.Format);
            Assert.NotEqual(afterRecovery.ETag, afterMigration.ETag);
            ((IDisposable)manager).Dispose();
        }

        await using var finalProvider = await CreateAzureProviderAsync(OrleansBinaryJournalFormat.JournalFormatKey, cts.Token);
        var finalStorage = finalProvider.StorageProvider.CreateStorage(journalId);
        var finalManager = CreateFormatAwareManager(finalProvider.ServiceProvider, finalStorage, OrleansBinaryJournalFormat.JournalFormatKey);
        var finalDictionary = CreateFormatAwareDictionary(finalProvider.ServiceProvider, finalManager, OrleansBinaryJournalFormat.JournalFormatKey);
        await finalManager.InitializeAsync(cts.Token);

        Assert.Equal(2, finalDictionary.Count);
        Assert.Equal(1, finalDictionary["alpha"]);
        Assert.Equal(2, finalDictionary["beta"]);
        await finalStorage.DeleteAsync(cts.Token);
        ((IDisposable)finalManager).Dispose();
    }

    [SkippableFact]
    public async Task AzureTableStorage_UnknownStoredFormat_ReportsKeyWithoutMutatingJournal()
    {
        const string unknownFormat = "unknown-table-journal-format";
        var grainId = GrainId.Create("journaling-table-unknown-format", Guid.NewGuid().ToString("N"));
        var journalId = JournalId.FromGrainId(grainId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await WriteDictionaryJournalAsync(journalId, OrleansBinaryJournalFormat.JournalFormatKey, cts.Token);
        await SetStoredFormatAsync(journalId, unknownFormat, cts.Token);

        await using var readerProvider = await CreateAzureProviderAsync(OrleansBinaryJournalFormat.JournalFormatKey, cts.Token);
        var storage = readerProvider.StorageProvider.CreateStorage(journalId);
        var before = await CaptureStoredJournalAsync(storage, cts.Token);
        var manager = CreateFormatAwareManager(readerProvider.ServiceProvider, storage, OrleansBinaryJournalFormat.JournalFormatKey);
        _ = CreateFormatAwareDictionary(readerProvider.ServiceProvider, manager, OrleansBinaryJournalFormat.JournalFormatKey);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.InitializeAsync(cts.Token).AsTask());

        Assert.Contains($"journal format key '{unknownFormat}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("none was registered", Assert.IsType<InvalidOperationException>(exception.InnerException).Message, StringComparison.Ordinal);
        ((IDisposable)manager).Dispose();

        var after = await CaptureStoredJournalAsync(readerProvider.StorageProvider.CreateStorage(journalId), cts.Token);
        AssertStoredJournalUnchanged(before, after);
        await storage.DeleteAsync(cts.Token);
    }

    [SkippableFact]
    public async Task AzureTableStorage_MismatchedStoredFormat_ReportsDeclaredAndConfiguredKeysWithoutMutation()
    {
        var grainId = GrainId.Create("journaling-table-mismatched-format", Guid.NewGuid().ToString("N"));
        var journalId = JournalId.FromGrainId(grainId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await WriteDictionaryJournalAsync(journalId, OrleansBinaryJournalFormat.JournalFormatKey, cts.Token);
        await SetStoredFormatAsync(journalId, JsonJournalExtensions.JournalFormatKey, cts.Token);

        await using var readerProvider = await CreateAzureProviderAsync(OrleansBinaryJournalFormat.JournalFormatKey, cts.Token);
        var storage = readerProvider.StorageProvider.CreateStorage(journalId);
        var before = await CaptureStoredJournalAsync(storage, cts.Token);
        var manager = CreateFormatAwareManager(readerProvider.ServiceProvider, storage, OrleansBinaryJournalFormat.JournalFormatKey);
        _ = CreateFormatAwareDictionary(readerProvider.ServiceProvider, manager, OrleansBinaryJournalFormat.JournalFormatKey);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.InitializeAsync(cts.Token).AsTask());

        Assert.Contains($"journal format key '{JsonJournalExtensions.JournalFormatKey}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"configured write journal format key is '{OrleansBinaryJournalFormat.JournalFormatKey}'", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.InnerException);
        ((IDisposable)manager).Dispose();

        var after = await CaptureStoredJournalAsync(readerProvider.StorageProvider.CreateStorage(journalId), cts.Token);
        AssertStoredJournalUnchanged(before, after);
        await storage.DeleteAsync(cts.Token);
    }

    [SkippableTheory]
    [InlineData(OrleansBinaryJournalFormat.JournalFormatKey)]
    [InlineData(JsonJournalExtensions.JournalFormatKey)]
    public async Task AzureTableStorage_TruncatedCodecPayload_PropagatesRecoveryFailureWithoutMutation(string journalFormatKey)
    {
        var grainId = GrainId.Create("journaling-table-truncated-payload", Guid.NewGuid().ToString("N"));
        var journalId = JournalId.FromGrainId(grainId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await using (var writerProvider = await CreateAzureProviderAsync(journalFormatKey, cts.Token))
        {
            var storage = writerProvider.StorageProvider.CreateStorage(journalId);
            var manager = CreateFormatAwareManager(writerProvider.ServiceProvider, storage, journalFormatKey);
            var dictionary = CreateFormatAwareDictionary(writerProvider.ServiceProvider, manager, journalFormatKey);
            await manager.InitializeAsync(cts.Token);
            dictionary.Add("alpha", 1);
            dictionary.Add("omega", 99);
            await manager.WriteStateAsync(cts.Token);
            ((IDisposable)manager).Dispose();

            var valid = await CaptureStoredJournalAsync(storage, cts.Token);
            var bytesToRemove = string.Equals(journalFormatKey, JsonJournalExtensions.JournalFormatKey, StringComparison.Ordinal) ? 2 : 1;
            await storage.ReplaceAsync(
                new ReadOnlySequence<byte>(valid.Bytes.AsMemory(0, valid.Bytes.Length - bytesToRemove)),
                cts.Token);
        }

        await using var readerProvider = await CreateAzureProviderAsync(journalFormatKey, cts.Token);
        var storageToRecover = readerProvider.StorageProvider.CreateStorage(journalId);
        var before = await CaptureStoredJournalAsync(storageToRecover, cts.Token);
        var managerToRecover = CreateFormatAwareManager(readerProvider.ServiceProvider, storageToRecover, journalFormatKey);
        _ = CreateFormatAwareDictionary(readerProvider.ServiceProvider, managerToRecover, journalFormatKey);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => managerToRecover.InitializeAsync(cts.Token).AsTask());

        Assert.Contains($"journal format key '{journalFormatKey}'", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.InnerException);
        ((IDisposable)managerToRecover).Dispose();

        var after = await CaptureStoredJournalAsync(readerProvider.StorageProvider.CreateStorage(journalId), cts.Token);
        AssertStoredJournalUnchanged(before, after);
        await storageToRecover.DeleteAsync(cts.Token);
    }

    private async Task WriteDictionaryJournalAsync(JournalId journalId, string journalFormatKey, CancellationToken cancellationToken)
    {
        await using var provider = await CreateAzureProviderAsync(journalFormatKey, cancellationToken);
        var storage = provider.StorageProvider.CreateStorage(journalId);
        var manager = CreateFormatAwareManager(provider.ServiceProvider, storage, journalFormatKey);
        var dictionary = CreateFormatAwareDictionary(provider.ServiceProvider, manager, journalFormatKey);
        await manager.InitializeAsync(cancellationToken);
        dictionary.Add("alpha", 1);
        dictionary.Add("beta", 2);
        await manager.WriteStateAsync(cancellationToken);
        ((IDisposable)manager).Dispose();
    }

    private static async Task SetStoredFormatAsync(JournalId journalId, string format, CancellationToken cancellationToken)
    {
        var options = new AzureTableJournalStorageOptions();
        JournalingAzureStorageTestConfiguration.ConfigureTestDefaults(options);
        var tableServiceClient = await options.CreateClient!(cancellationToken);
        var table = tableServiceClient.GetTableClient(options.TableName);
        var partitionKey = options.GetPartitionKeyForJournal(journalId);
        var patch = new Azure.Data.Tables.TableEntity(partitionKey, AzureTableJournalStorage.HeaderRowKey)
        {
            [AzureTableJournalStorage.FormatPropertyName] = format,
        };

        await table.UpdateEntityAsync(patch, Azure.ETag.All, Azure.Data.Tables.TableUpdateMode.Merge, cancellationToken);
    }

    private static async Task<StoredJournalSnapshot> CaptureStoredJournalAsync(
        IJournalStorage storage,
        CancellationToken cancellationToken)
    {
        var metadata = Assert.IsAssignableFrom<IJournalMetadata>(await storage.GetMetadataAsync(cancellationToken));
        var consumer = new RecordingJournalStorageConsumer();
        await storage.ReadAsync(consumer, cancellationToken);
        Assert.True(consumer.IsCompleted);
        Assert.NotEmpty(consumer.Formats);
        Assert.All(consumer.Formats, format => Assert.Equal(metadata.Format, format));
        return new(metadata.Format, metadata.ETag, consumer.Bytes.ToArray());
    }

    private static void AssertStoredJournalUnchanged(StoredJournalSnapshot before, StoredJournalSnapshot after)
    {
        Assert.Equal(before.Format, after.Format);
        Assert.Equal(before.ETag, after.ETag);
        Assert.Equal(before.Bytes, after.Bytes);
    }

    private sealed record StoredJournalSnapshot(string? Format, string? ETag, byte[] Bytes);
}
