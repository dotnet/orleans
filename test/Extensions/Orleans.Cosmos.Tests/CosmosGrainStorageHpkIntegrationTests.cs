using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Orleans.Configuration;
using Orleans.Persistence.Cosmos;
using Orleans.Runtime;
using Orleans.Storage;
using TestExtensions;

namespace Tester.Cosmos.Persistence;

/// <summary>
/// Tests hierarchical partition keys against Azure Cosmos DB or the Cosmos DB emulator.
/// </summary>
[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("Persistence"), TestCategory("Cosmos")]
[TestSuite("Functional")]
[TestProvider("Cosmos")]
[TestArea("Persistence")]
public class CosmosGrainStorageHpkIntegrationTests
{
    private readonly IServiceProvider _services;

    public CosmosGrainStorageHpkIntegrationTests(TestEnvironmentFixture fixture)
    {
        CosmosTestUtils.CheckCosmosStorage();
        _services = fixture.Services;
    }

    [Theory, TestCategory("Functional")]
    [InlineData(2)]
    [InlineData(3)]
    public async Task HierarchicalPartitionKeys_SupportFullLifecycleAndOptimisticConcurrency(int partitionKeyLevelCount)
    {
        var databaseName = $"OrleansHpk{Guid.NewGuid():N}";
        const string containerName = "GrainState";
        var clusterOptions = Options.Create(new ClusterOptions { ClusterId = "cluster", ServiceId = "service" });
        var documentIdProvider = new HierarchicalDocumentIdProvider(clusterOptions, partitionKeyLevelCount);
        var options = CreateOptions(databaseName, containerName, partitionKeyLevelCount, deleteStateOnClear: false);
        var client = await options.CreateClient(_services);

        try
        {
            var storage = await StartStorage(options, clusterOptions, documentIdProvider);
            var grainId = GrainId.Create("test-grain", Guid.NewGuid().ToString("N"));
            const string grainType = "test-grain-type";
            var state = new GrainState<TestState> { State = new TestState { Value = 1 } };

            await storage.WriteStateAsync(grainType, grainId, state);
            Assert.True(state.RecordExists);
            Assert.False(string.IsNullOrWhiteSpace(state.ETag));

            var storedState = new GrainState<TestState> { State = new TestState() };
            await storage.ReadStateAsync(grainType, grainId, storedState);
            Assert.True(storedState.RecordExists);
            Assert.Equal(1, storedState.State.Value);

            var documentKey = await documentIdProvider.GetDocumentKey(grainType, grainId);
            var partitionKey = BuildPartitionKey(documentKey.PartitionKeyValues);
            var document = await client.GetContainer(databaseName, containerName).ReadItemAsync<JObject>(
                documentKey.DocumentId,
                partitionKey,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("tenant", document.Resource.Value<string>("PartitionKey"));
            Assert.Equal(grainType, document.Resource.Value<string>("PartitionKey2"));
            Assert.Equal(partitionKeyLevelCount == 3 ? "region" : null, document.Resource.Value<string>("PartitionKey3"));

            var staleEtag = storedState.ETag;
            storedState.State.Value = 2;
            await storage.WriteStateAsync(grainType, grainId, storedState);

            var staleState = new GrainState<TestState>
            {
                ETag = staleEtag,
                State = new TestState { Value = 3 }
            };
            await Assert.ThrowsAsync<CosmosConditionNotSatisfiedException>(
                () => storage.WriteStateAsync(grainType, grainId, staleState));

            await storage.ClearStateAsync(grainType, grainId, storedState);
            Assert.False(storedState.RecordExists);
            var clearedState = new GrainState<TestState> { State = new TestState() };
            await storage.ReadStateAsync(grainType, grainId, clearedState);
            Assert.False(clearedState.RecordExists);

            var deleteOptions = CreateOptions(databaseName, containerName, partitionKeyLevelCount, deleteStateOnClear: true);
            var deleteStorage = await StartStorage(deleteOptions, clusterOptions, documentIdProvider);
            var deleteGrainId = GrainId.Create("test-grain", Guid.NewGuid().ToString("N"));
            var deleteState = new GrainState<TestState> { State = new TestState { Value = 4 } };
            await deleteStorage.WriteStateAsync(grainType, deleteGrainId, deleteState);
            var deleteDocumentKey = await documentIdProvider.GetDocumentKey(grainType, deleteGrainId);
            await deleteStorage.ClearStateAsync(grainType, deleteGrainId, deleteState);

            var exception = await Assert.ThrowsAsync<CosmosException>(() =>
                client.GetContainer(databaseName, containerName).ReadItemAsync<JObject>(
                    deleteDocumentKey.DocumentId,
                    BuildPartitionKey(deleteDocumentKey.PartitionKeyValues),
                    cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        }
        finally
        {
            await DeleteDatabase(client, databaseName);
            client.Dispose();
        }
    }

    [Theory, TestCategory("Functional")]
    [InlineData(SinglePartitionKeyProviderKind.Default)]
    [InlineData(SinglePartitionKeyProviderKind.ExistingDocumentIdProvider)]
    [InlineData(SinglePartitionKeyProviderKind.LegacyPartitionKeyProvider)]
    public async Task SinglePartitionKeyMode_PreservesExistingProviderBehavior(SinglePartitionKeyProviderKind providerKind)
    {
        var databaseName = $"OrleansSingleKey{Guid.NewGuid():N}";
        const string containerName = "GrainState";
        var clusterOptions = Options.Create(new ClusterOptions { ClusterId = "cluster", ServiceId = "service" });
        var options = CreateOptions(databaseName, containerName, 1, deleteStateOnClear: false);
        var client = await options.CreateClient(_services);
        var defaultProvider = new DefaultDocumentIdProvider(clusterOptions);
        IDocumentIdProvider documentIdProvider = providerKind switch
        {
            SinglePartitionKeyProviderKind.Default => defaultProvider,
            SinglePartitionKeyProviderKind.ExistingDocumentIdProvider => new ExistingDocumentIdProvider(defaultProvider),
#pragma warning disable CS0618 // Type or member is obsolete
            _ => new DefaultDocumentIdProvider(clusterOptions, new LegacyPartitionKeyProvider())
#pragma warning restore CS0618 // Type or member is obsolete
        };

        try
        {
            var storage = await StartStorage(options, clusterOptions, documentIdProvider);
            var properties = await client.GetContainer(databaseName, containerName).ReadContainerAsync(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(["/PartitionKey"], properties.Resource.PartitionKeyPaths);

            var grainId = GrainId.Create("test-grain", Guid.NewGuid().ToString("N"));
            var state = new GrainState<TestState> { State = new TestState { Value = 5 } };
            await storage.WriteStateAsync("grain-type", grainId, state);
            var readState = new GrainState<TestState> { State = new TestState() };
            await storage.ReadStateAsync("grain-type", grainId, readState);

            Assert.True(readState.RecordExists);
            Assert.Equal(5, readState.State.Value);
        }
        finally
        {
            await DeleteDatabase(client, databaseName);
            client.Dispose();
        }
    }

    [Fact, TestCategory("Functional")]
    public async Task CustomSinglePartitionKeyPath_RemainsSupported()
    {
        var databaseName = $"OrleansCustomPath{Guid.NewGuid():N}";
        const string containerName = "GrainState";
        var clusterOptions = Options.Create(new ClusterOptions { ClusterId = "cluster", ServiceId = "service" });
        var options = CreateOptions(databaseName, containerName, 1, deleteStateOnClear: false);
        options.PartitionKeyPath = "/GrainType";
        var client = await options.CreateClient(_services);

        try
        {
            var storage = await StartStorage(options, clusterOptions, new DefaultDocumentIdProvider(clusterOptions));
            var properties = await client.GetContainer(databaseName, containerName).ReadContainerAsync(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(["/GrainType"], properties.Resource.PartitionKeyPaths);

            var grainId = GrainId.Create("test-grain", Guid.NewGuid().ToString("N"));
            var state = new GrainState<TestState> { State = new TestState { Value = 6 } };
            await storage.WriteStateAsync("grain-type", grainId, state);
            var readState = new GrainState<TestState> { State = new TestState() };
            await storage.ReadStateAsync("grain-type", grainId, readState);

            Assert.True(readState.RecordExists);
            Assert.Equal(6, readState.State.Value);
        }
        finally
        {
            await DeleteDatabase(client, databaseName);
            client.Dispose();
        }
    }

    [Theory, TestCategory("Functional")]
    [MemberData(nameof(MismatchedContainerDefinitions))]
    public async Task Startup_RejectsMismatchedContainerPartitionKeyDefinition(
        int configuredLevelCount,
        string[] containerPaths,
        bool resourceCreationEnabled)
    {
        var databaseName = $"OrleansHpkMismatch{Guid.NewGuid():N}";
        const string containerName = "GrainState";
        var clusterOptions = Options.Create(new ClusterOptions { ClusterId = "cluster", ServiceId = "service" });
        var options = CreateOptions(databaseName, containerName, configuredLevelCount, deleteStateOnClear: false);
        var client = await options.CreateClient(_services);

        try
        {
            await CreateContainer(client, databaseName, containerName, containerPaths);
            options.IsResourceCreationEnabled = resourceCreationEnabled;
            var provider = new HierarchicalDocumentIdProvider(clusterOptions, configuredLevelCount);

            var exception = await Assert.ThrowsAsync<WrappedException>(
                () => StartStorage(options, clusterOptions, provider));

            Assert.EndsWith(nameof(OrleansConfigurationException), exception.OriginalExceptionType);
            Assert.Contains("number, order, and name", exception.Message);
        }
        finally
        {
            await DeleteDatabase(client, databaseName);
            client.Dispose();
        }
    }

    [Theory, TestCategory("Functional")]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public async Task Startup_AcceptsMatchingHierarchicalPartitionKeyDefinition(int partitionKeyLevelCount, bool resourceCreationEnabled)
    {
        var databaseName = $"OrleansHpkMatch{Guid.NewGuid():N}";
        const string containerName = "GrainState";
        var clusterOptions = Options.Create(new ClusterOptions { ClusterId = "cluster", ServiceId = "service" });
        var options = CreateOptions(databaseName, containerName, partitionKeyLevelCount, deleteStateOnClear: false);
        var client = await options.CreateClient(_services);

        try
        {
            var paths = GetHierarchicalPaths(partitionKeyLevelCount);
            await CreateContainer(client, databaseName, containerName, paths);
            options.IsResourceCreationEnabled = resourceCreationEnabled;
            var provider = new HierarchicalDocumentIdProvider(clusterOptions, partitionKeyLevelCount);

            var storage = await StartStorage(options, clusterOptions, provider);

            Assert.NotNull(storage);
        }
        finally
        {
            await DeleteDatabase(client, databaseName);
            client.Dispose();
        }
    }

    public static TheoryData<int, string[], bool> MismatchedContainerDefinitions => new()
    {
        { 2, ["/PartitionKey"], true },
        { 1, ["/PartitionKey", "/PartitionKey2"], true },
        { 2, ["/PartitionKey", "/PartitionKey2", "/PartitionKey3"], true },
        { 2, ["/PartitionKey2", "/PartitionKey"], true },
        { 2, ["/PartitionKey", "/Different"], true },
        { 2, ["/PartitionKey"], false }
    };

    private CosmosGrainStorageOptions CreateOptions(
        string databaseName,
        string containerName,
        int partitionKeyLevelCount,
        bool deleteStateOnClear)
    {
        var options = new CosmosGrainStorageOptions
        {
            DatabaseName = databaseName,
            ContainerName = containerName,
            PartitionKeyLevelCount = partitionKeyLevelCount,
            DeleteStateOnClear = deleteStateOnClear
        };
        options.ConfigureTestDefaults();
        return options;
    }

    private async Task<CosmosGrainStorage> StartStorage(
        CosmosGrainStorageOptions options,
        IOptions<ClusterOptions> clusterOptions,
        IDocumentIdProvider documentIdProvider)
    {
        var storage = ActivatorUtilities.CreateInstance<CosmosGrainStorage>(
            _services,
            options,
            clusterOptions,
            "TestStorage",
            documentIdProvider);
        var lifecycle = ActivatorUtilities.CreateInstance<SiloLifecycleSubject>(_services);
        storage.Participate(lifecycle);
        await lifecycle.OnStart();
        return storage;
    }

    private static async Task CreateContainer(
        CosmosClient client,
        string databaseName,
        string containerName,
        IReadOnlyList<string> partitionKeyPaths)
    {
        var database = await client.CreateDatabaseIfNotExistsAsync(databaseName);
        var properties = partitionKeyPaths.Count == 1
            ? new ContainerProperties(containerName, partitionKeyPaths[0])
            : new ContainerProperties(containerName, partitionKeyPaths);
        await database.Database.CreateContainerIfNotExistsAsync(properties);
    }

    private static PartitionKey BuildPartitionKey(IReadOnlyList<string> values)
    {
        if (values.Count == 1)
        {
            return new PartitionKey(values[0]);
        }

        var builder = new PartitionKeyBuilder();
        foreach (var value in values)
        {
            builder.Add(value);
        }

        return builder.Build();
    }

    private static string[] GetHierarchicalPaths(int partitionKeyLevelCount) =>
        (new[] { "/PartitionKey", "/PartitionKey2", "/PartitionKey3" })[..partitionKeyLevelCount];

    private static async Task DeleteDatabase(CosmosClient client, string databaseName)
    {
        try
        {
            await client.GetDatabase(databaseName).DeleteAsync();
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private sealed class HierarchicalDocumentIdProvider : IDocumentIdProvider
    {
        private readonly DefaultDocumentIdProvider _defaultProvider;
        private readonly int _partitionKeyLevelCount;

        public HierarchicalDocumentIdProvider(IOptions<ClusterOptions> clusterOptions, int partitionKeyLevelCount)
        {
            _defaultProvider = new DefaultDocumentIdProvider(clusterOptions);
            _partitionKeyLevelCount = partitionKeyLevelCount;
        }

        public ValueTask<(string DocumentId, string PartitionKey)> GetDocumentIdentifiers(string grainType, GrainId grainId) =>
            new((_defaultProvider.GetId(grainType, grainId), "tenant"));

        public ValueTask<CosmosDocumentKey> GetDocumentKey(string grainType, GrainId grainId)
        {
            var values = _partitionKeyLevelCount switch
            {
                1 => new[] { "tenant" },
                2 => new[] { "tenant", grainType },
                _ => new[] { "tenant", grainType, "region" }
            };
            return new(new CosmosDocumentKey(_defaultProvider.GetId(grainType, grainId), values));
        }
    }

    private sealed class ExistingDocumentIdProvider(DefaultDocumentIdProvider defaultProvider) : IDocumentIdProvider
    {
        public ValueTask<(string DocumentId, string PartitionKey)> GetDocumentIdentifiers(string grainType, GrainId grainId) =>
            new((defaultProvider.GetId(grainType, grainId), "custom-partition"));
    }

#pragma warning disable CS0618 // Type or member is obsolete
    private sealed class LegacyPartitionKeyProvider : IPartitionKeyProvider
    {
        public ValueTask<string> GetPartitionKey(string grainType, GrainId grainId) => new("legacy-partition");
    }
#pragma warning restore CS0618 // Type or member is obsolete

    public enum SinglePartitionKeyProviderKind
    {
        Default,
        ExistingDocumentIdProvider,
        LegacyPartitionKeyProvider
    }

    private sealed class TestState
    {
        public int Value { get; set; }
    }
}
