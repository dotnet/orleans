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
public sealed class AzureBlobCodecRecoveryTests : JournalingTestBase, IAsyncLifetime
{
    private ServiceProvider _azureServiceProvider = null!;
    private SiloLifecycleSubject _siloLifecycle = null!;
    private AzureBlobJournalStorageProvider _storageProvider = null!;

    public AzureBlobCodecRecoveryTests()
    {
        JournalingAzureStorageTestConfiguration.CheckPreconditionsOrThrow();
    }

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AzureBlobJournalStorageOptions>(options => JournalingAzureStorageTestConfiguration.ConfigureTestDefaults(options));
        services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey);
        services.AddSerializer();
        ConfigureFormatServices(services);
        services.AddSingleton<AzureBlobJournalStorageProvider>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, AzureBlobJournalStorageProvider>();

        _azureServiceProvider = services.BuildServiceProvider();
        _storageProvider = _azureServiceProvider.GetRequiredService<AzureBlobJournalStorageProvider>();
        _siloLifecycle = new SiloLifecycleSubject(_azureServiceProvider.GetRequiredService<ILogger<SiloLifecycleSubject>>());

        foreach (var participant in _azureServiceProvider.GetServices<ILifecycleParticipant<ISiloLifecycle>>())
        {
            participant.Participate(_siloLifecycle);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await _siloLifecycle.OnStart(cts.Token);
    }

    [SkippableFact]
    public async Task AzureBlobStorage_BinaryJournal_MigratesToJsonOnFirstWrite()
    {
        var blobName = $"journaling-codec-migration/{Guid.NewGuid():N}";
        var grainId = GrainId.Create("journaling-codec-migration", "0");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await using (var binaryProvider = await CreateAzureProviderAsync(OrleansBinaryJournalFormat.JournalFormatKey, blobName, cts.Token))
        {
            var storage = binaryProvider.StorageProvider.Create(new JournalBatchTests.TestGrainContext(grainId));
            var manager = CreateFormatAwareManager(binaryProvider.ServiceProvider, storage, OrleansBinaryJournalFormat.JournalFormatKey);
            var dict = CreateFormatAwareDictionary(binaryProvider.ServiceProvider, manager, OrleansBinaryJournalFormat.JournalFormatKey);
            await manager.InitializeAsync(cts.Token);

            dict.Add("alpha", 1);
            await manager.WriteStateAsync(cts.Token);
            ((IDisposable)manager).Dispose();
        }

        await using var jsonProvider = await CreateAzureProviderAsync(JsonJournalExtensions.JournalFormatKey, blobName, cts.Token);
        var migratedStorage = jsonProvider.StorageProvider.Create(new JournalBatchTests.TestGrainContext(grainId));
        var migratedManager = CreateFormatAwareManager(jsonProvider.ServiceProvider, migratedStorage, JsonJournalExtensions.JournalFormatKey);
        var migratedDict = CreateFormatAwareDictionary(jsonProvider.ServiceProvider, migratedManager, JsonJournalExtensions.JournalFormatKey);
        await migratedManager.InitializeAsync(cts.Token);

        Assert.Equal(1, migratedDict["alpha"]);

        migratedDict.Add("beta", 2);
        await migratedManager.WriteStateAsync(cts.Token);
        ((IDisposable)migratedManager).Dispose();

        var recoveredStorage = jsonProvider.StorageProvider.Create(new JournalBatchTests.TestGrainContext(grainId));
        var recoveredManager = CreateFormatAwareManager(jsonProvider.ServiceProvider, recoveredStorage, JsonJournalExtensions.JournalFormatKey);
        var recoveredDict = CreateFormatAwareDictionary(jsonProvider.ServiceProvider, recoveredManager, JsonJournalExtensions.JournalFormatKey);
        await recoveredManager.InitializeAsync(cts.Token);

        Assert.Equal(1, recoveredDict["alpha"]);
        Assert.Equal(2, recoveredDict["beta"]);
        await recoveredStorage.DeleteAsync(cts.Token);
        ((IDisposable)recoveredManager).Dispose();
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
    public async Task AzureBlobStorage_AllDurableTypes_RecoverWithBinaryCodec()
    {
        var grainId = GrainId.Create("journaling-codec-recovery", Guid.NewGuid().ToString("N"));
        var storage = _storageProvider.Create(new JournalBatchTests.TestGrainContext(grainId));
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

        var recoveredStorage = _storageProvider.Create(new JournalBatchTests.TestGrainContext(grainId));
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
            storage,
            ServiceProvider);

        return new(shared);
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
        services.AddSingleton(new JsonJournalOptions { SerializerOptions = jsonOptions });
        services.AddKeyedSingleton<IJournalFormat>(JsonJournalExtensions.JournalFormatKey, new JsonLinesJournalFormat());
        services.AddKeyedSingleton(
            typeof(IDurableDictionaryCommandCodec<,>),
            JsonJournalExtensions.JournalFormatKey,
            typeof(JsonDurableDictionaryCommandCodecService<,>));
    }

    private async Task<AzureProviderFixture> CreateAzureProviderAsync(string journalFormatKey, string blobName, CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AzureBlobJournalStorageOptions>(options =>
        {
            JournalingAzureStorageTestConfiguration.ConfigureTestDefaults(options);
            options.GetBlobName = _ => blobName;
        });
        services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = journalFormatKey);
        services.AddSerializer();
        ConfigureFormatServices(services);
        services.AddSingleton<AzureBlobJournalStorageProvider>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, AzureBlobJournalStorageProvider>();

        var serviceProvider = services.BuildServiceProvider();
        var lifecycle = new SiloLifecycleSubject(serviceProvider.GetRequiredService<ILogger<SiloLifecycleSubject>>());
        foreach (var participant in serviceProvider.GetServices<ILifecycleParticipant<ISiloLifecycle>>())
        {
            participant.Participate(lifecycle);
        }

        await lifecycle.OnStart(cancellationToken);
        return new(serviceProvider, lifecycle, serviceProvider.GetRequiredService<AzureBlobJournalStorageProvider>());
    }

    private static JournaledStateManager CreateFormatAwareManager(IServiceProvider serviceProvider, IJournalStorage storage, string journalFormatKey)
    {
        var shared = new JournaledStateManagerShared(
            serviceProvider.GetRequiredService<ILogger<JournaledStateManager>>(),
            Options.Create(new JournaledStateManagerOptions { JournalFormatKey = journalFormatKey }),
            TimeProvider.System,
            storage,
            serviceProvider);

        return new(shared);
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
        AzureBlobJournalStorageProvider storageProvider) : IAsyncDisposable
    {
        public ServiceProvider ServiceProvider { get; } = serviceProvider;

        public AzureBlobJournalStorageProvider StorageProvider { get; } = storageProvider;

        public async ValueTask DisposeAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await lifecycle.OnStop(cts.Token);
            await ServiceProvider.DisposeAsync();
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
}
