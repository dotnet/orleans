using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Internal;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using TestExtensions;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("AzureStorage"), TestCategory("Functional")]
public sealed class AzureStorageLogSegmentTests : LogSegmentTests
{
    public AzureStorageLogSegmentTests()
    {
        JournalingAzureStorageTestConfiguration.CheckPreconditionsOrThrow();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.Configure<AzureAppendBlobStateMachineStorageOptions>(options => JournalingAzureStorageTestConfiguration.ConfigureTestDefaults(options));
        services.AddSingleton<AzureAppendBlobStateMachineStorageProvider>();
        services.AddFromExisting<IStateMachineStorageProvider, AzureAppendBlobStateMachineStorageProvider>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, AzureAppendBlobStateMachineStorageProvider>();
    }
}

public sealed class InMemoryLogSegmentTests : LogSegmentTests
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IStateMachineStorageProvider, VolatileStateMachineStorageProvider>();
    }
}

/// <summary>
/// Base class for testing <see cref="IStateMachineStorageProvider"/> implementations.
/// Derived classes must implement <see cref="ConfigureServices"/> to register the specific storage provider.
/// This class provides a suite of common tests for validating the behavior of <see cref="DurableList{T}"/>
/// against different storage backends.
/// </summary>
public abstract class LogSegmentTests : IAsyncLifetime
{
    private IServiceProvider _serviceProvider = null!;
    private SiloLifecycleSubject? _siloLifecycle;
    private IStateMachineStorageProvider _storageProvider = null!;

    public virtual async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddLogging();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();
        _siloLifecycle = new SiloLifecycleSubject(_serviceProvider.GetRequiredService<ILogger<SiloLifecycleSubject>>());
        _storageProvider = _serviceProvider.GetRequiredService<IStateMachineStorageProvider>();
        var participants = _serviceProvider.GetServices<ILifecycleParticipant<ISiloLifecycle>>();
        foreach (var participant in participants)
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
    }

    protected abstract void ConfigureServices(IServiceCollection services);

    private (StateMachineManager Manager, DurableList<T> List, IStateMachineStorage Storage) CreateTestComponents<T>(string listName, GrainId grainId)
    {
        var sessionPool = _serviceProvider.GetRequiredService<SerializerSessionPool>();
        var codecProvider = _serviceProvider.GetRequiredService<ICodecProvider>();
        var grainContext = new TestGrainContext(grainId); // Use provided GrainId
        var storage = _storageProvider.Create(grainContext);
        var manager = new StateMachineManager(storage, _serviceProvider.GetRequiredService<ILogger<StateMachineManager>>(), sessionPool);
        var list = new DurableList<T>(listName, manager, codecProvider.GetCodec<T>(), sessionPool);
        return (manager, list, storage);
    }

    /// <summary>
    /// Tests basic Add, Update (by index), and RemoveAt operations.
    /// </summary>
    [SkippableFact]
    public async Task DurableList_BasicOperations_Test()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var grainId = GrainId.Create("test-grain", $"basicList-{Guid.NewGuid()}"); // Unique ID for this test run
        var (manager, list, _) = CreateTestComponents<string>("basicList", grainId);
        await manager.InitializeAsync(cts.Token);

        list.Add("one");
        list.Add("two");
        list.Add("three");
        await manager.WriteStateAsync(cts.Token);

        Assert.Equal(3, list.Count);
        Assert.Equal("one", list[0]);
        Assert.Equal("two", list[1]);
        Assert.Equal("three", list[2]);

        list[1] = "updated";
        await manager.WriteStateAsync(cts.Token);

        Assert.Equal("updated", list[1]);

        list.RemoveAt(0);
        await manager.WriteStateAsync(cts.Token);

        Assert.Equal(2, list.Count);
        Assert.Equal("updated", list[0]);
        Assert.Equal("three", list[1]);
    }

    /// <summary>
    /// Tests that list state is correctly persisted and can be recovered by a new instance.
    /// </summary>
    [SkippableFact]
    public async Task DurableList_Persistence_Test()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listName = "persistenceList";
        var grainId = GrainId.Create("test-grain", $"{listName}-{Guid.NewGuid()}"); // Consistent GrainId for recovery
        var (manager1, list1, storage) = CreateTestComponents<string>(listName, grainId);
        await manager1.InitializeAsync(cts.Token);

        list1.Add("one");
        list1.Add("two");
        list1.Add("three");
        await manager1.WriteStateAsync(cts.Token);

        var sessionPool = _serviceProvider.GetRequiredService<SerializerSessionPool>();
        var codecProvider = _serviceProvider.GetRequiredService<ICodecProvider>();
        var manager2 = new StateMachineManager(storage, _serviceProvider.GetRequiredService<ILogger<StateMachineManager>>(), sessionPool);
        var list2 = new DurableList<string>(listName, manager2, codecProvider.GetCodec<string>(), sessionPool);
        await manager2.InitializeAsync(cts.Token);

        Assert.Equal(3, list2.Count);
        Assert.Equal("one", list2[0]);
        Assert.Equal("two", list2[1]);
        Assert.Equal("three", list2[2]);
    }

    /// <summary>
    /// Tests storing and retrieving complex objects, including updates to mutable properties.
    /// </summary>
    [SkippableFact]
    public async Task DurableList_ComplexValues_Test()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listName = "personList";
        var grainId = GrainId.Create("test-grain", $"{listName}-{Guid.NewGuid()}"); // Consistent GrainId for recovery
        var (manager, list, _) = CreateTestComponents<TestPerson>(listName, grainId);
        await manager.InitializeAsync(cts.Token);

        var person1 = new TestPerson { Id = 1, Name = "John", Age = 30 };
        var person2 = new TestPerson { Id = 2, Name = "Jane", Age = 25 };

        list.Add(person1);
        list.Add(person2);
        await manager.WriteStateAsync(cts.Token);

        Assert.Equal(2, list.Count);
        Assert.Equal("John", list[0].Name);
        Assert.Equal(25, list[1].Age);

        list[0] = list[0] with { Age = 31 };
        await manager.WriteStateAsync(cts.Token);

        // Re-read to confirm persistence of the change
        var (manager2, list2, _) = CreateTestComponents<TestPerson>(listName, grainId); // Use same GrainId to reload
        await manager2.InitializeAsync(cts.Token);
        Assert.Equal(31, list2[0].Age);
    }

    /// <summary>
    /// Tests the Clear operation and its persistence.
    /// </summary>
    [SkippableFact]
    public async Task DurableList_Clear_Test()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listName = "clearList";
        var grainId = GrainId.Create("test-grain", $"{listName}-{Guid.NewGuid()}"); // Consistent GrainId for recovery
        var (manager, list, _) = CreateTestComponents<string>(listName, grainId);
        await manager.InitializeAsync(cts.Token);

        list.Add("one");
        list.Add("two");
        list.Add("three");
        await manager.WriteStateAsync(cts.Token);

        list.Clear();
        await manager.WriteStateAsync(cts.Token);

        Assert.Empty(list);

        // Verify persistence of Clear
        var (manager2, list2, _) = CreateTestComponents<string>(listName, grainId); // Use same GrainId to reload
        await manager2.InitializeAsync(cts.Token);
        Assert.Empty(list2);
    }

    /// <summary>
    /// Tests the Contains method and Remove (by value) operation.
    /// </summary>
    [SkippableFact]
    public async Task DurableList_Contains_Test()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var grainId = GrainId.Create("test-grain", $"containsList-{Guid.NewGuid()}"); // Unique ID for this test run
        var (manager, list, _) = CreateTestComponents<string>("containsList", grainId);
        await manager.InitializeAsync(cts.Token);

        list.Add("one");
        list.Add("two");
        list.Add("three");
        await manager.WriteStateAsync(cts.Token);

        Assert.Contains("two", list);
        Assert.DoesNotContain("four", list);

        list.Remove("two");
        await manager.WriteStateAsync(cts.Token);

        Assert.DoesNotContain("two", list);
    }

    /// <summary>
    /// Tests Insert and Remove (by value) operations.
    /// </summary>
    [SkippableFact]
    public async Task DurableList_InsertAndRemove_Test()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var grainId = GrainId.Create("test-grain", $"insertList-{Guid.NewGuid()}"); // Unique ID for this test run
        var (manager, list, _) = CreateTestComponents<string>("insertList", grainId);
        await manager.InitializeAsync(cts.Token);

        list.Add("one");
        list.Add("three");
        await manager.WriteStateAsync(cts.Token);

        list.Insert(1, "two");
        await manager.WriteStateAsync(cts.Token);

        Assert.Equal(3, list.Count);
        Assert.Equal("one", list[0]);
        Assert.Equal("two", list[1]);
        Assert.Equal("three", list[2]);

        bool removed = list.Remove("two");
        await manager.WriteStateAsync(cts.Token);

        Assert.True(removed);
        Assert.Equal(2, list.Count);
        Assert.Equal("one", list[0]);
        Assert.Equal("three", list[1]);
    }

    /// <summary>
    /// Tests list enumeration using ToList() (which relies on GetEnumerator).
    /// </summary>
    [SkippableFact]
    public async Task DurableList_Enumeration_Test()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var grainId = GrainId.Create("test-grain", $"enumList-{Guid.NewGuid()}"); // Unique ID for this test run
        var (manager, list, _) = CreateTestComponents<string>("enumList", grainId);
        await manager.InitializeAsync(cts.Token);

        var expectedItems = new List<string> { "one", "two", "three" };

        foreach (var item in expectedItems)
        {
            list.Add(item);
        }

        await manager.WriteStateAsync(cts.Token);

        var actualItems = list.ToList();

        Assert.Equal(expectedItems, actualItems);
    }

    /// <summary>
    /// Tests behavior with a larger number of operations (add, update) and multiple writes,
    /// potentially triggering snapshotting behavior in the storage provider. Also tests recovery.
    /// </summary>
    [SkippableFact]
    public async Task DurableList_LargeNumberOfOperations_And_Snapshot_Test()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); // Increased timeout
        var listName = "largeList";
        var grainId = GrainId.Create("test-grain", $"{listName}-{Guid.NewGuid()}"); // Consistent GrainId for recovery
        var (manager, list, storage) = CreateTestComponents<int>(listName, grainId);
        await manager.InitializeAsync(cts.Token);

        const int itemCount = 100; // Reduced for faster testing, increase if needed
        for (int i = 0; i < itemCount; i++)
        {
            list.Add(i);
        }

        // Write multiple times to potentially trigger snapshotting
        for (int j = 0; j < 5; ++j)
        {
            await manager.WriteStateAsync(cts.Token);
        }

        Assert.Equal(itemCount, list.Count);

        for (int i = 0; i < itemCount; i += 2)
        {
            list[i] = list[i] * 2;
        }

        // Write multiple times again
        for (int j = 0; j < 5; ++j)
        {
            await manager.WriteStateAsync(cts.Token);
        }

        for (int i = 0; i < itemCount; i++)
        {
            if (i % 2 == 0) Assert.Equal(i * 2, list[i]);
            else Assert.Equal(i, list[i]);
        }

        // Test recovery (potentially from snapshot)
        var sessionPool = _serviceProvider.GetRequiredService<SerializerSessionPool>();
        var codecProvider = _serviceProvider.GetRequiredService<ICodecProvider>();
        var manager2 = new StateMachineManager(storage, _serviceProvider.GetRequiredService<ILogger<StateMachineManager>>(), sessionPool); // Reuses the storage object linked via grainId
        var list2 = new DurableList<int>(listName, manager2, codecProvider.GetCodec<int>(), sessionPool);
        await manager2.InitializeAsync(cts.Token);

        Assert.Equal(itemCount, list2.Count);
        for (int i = 0; i < itemCount; i++)
        {
            if (i % 2 == 0) Assert.Equal(i * 2, list2[i]);
            else Assert.Equal(i, list2[i]);
        }
    }

    // Keep TestGrainContext and add TestPerson record needed for one of the tests
    [GenerateSerializer, Immutable]
    internal sealed record TestPerson
    {
        [Id(0)]
        public int Id { get; init; }
        [Id(1)]
        public string Name { get; init; } = "";
        [Id(2)]
        public int Age { get; init; }
    }

    internal sealed class TestGrainContext(GrainId grainId) : IGrainContext
    {
        public GrainReference GrainReference => throw new NotImplementedException();
        public GrainId GrainId => grainId;
        public object? GrainInstance  => throw new NotImplementedException();
        public ActivationId ActivationId  => throw new NotImplementedException();
        public GrainAddress Address  => throw new NotImplementedException();
        public IServiceProvider ActivationServices  => throw new NotImplementedException();
        public IGrainLifecycle ObservableLifecycle  => throw new NotImplementedException();
        public IWorkItemScheduler Scheduler  => throw new NotImplementedException();
        public Task Deactivated  => throw new NotImplementedException();

        public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public void Deactivate(DeactivationReason deactivationReason, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public bool Equals(IGrainContext? other) => throw new NotImplementedException();
        public TComponent? GetComponent<TComponent>() where TComponent : class => throw new NotImplementedException();
        public TTarget? GetTarget<TTarget>() where TTarget : class => throw new NotImplementedException();
        public void Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public void ReceiveMessage(object message) => throw new NotImplementedException();
        public void Rehydrate(IRehydrationContext context) => throw new NotImplementedException();
        public void SetComponent<TComponent>(TComponent? value) where TComponent : class => throw new NotImplementedException();
    }
}
