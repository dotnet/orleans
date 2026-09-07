using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Persistence.Cosmos;
using Orleans.Runtime;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Serializers;
using Orleans.Storage;

namespace Tester.Cosmos.Persistence;

[TestCategory("Cosmos"), TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("Cosmos")]
[TestArea("Persistence")]
public sealed class CosmosGrainStorageArgumentValidationTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider = new ServiceCollection().BuildServiceProvider();

    [Fact]
    public void CosmosGrainStorage_Constructor_NullOptions_ThrowsArgumentNullException()
    {
        var loggerFactory = new TrackingLoggerFactory();

        var exception = Assert.Throws<ArgumentNullException>(() => new CosmosGrainStorage(
            "storage",
            null!,
            loggerFactory,
            _serviceProvider,
            CreateClusterOptions(),
            new TrackingDocumentIdProvider(),
            new ActivatorProvider()));

        Assert.Equal("options", exception.ParamName);
        Assert.Equal(0, loggerFactory.CreateLoggerCallCount);
    }

    [Fact]
    public void CosmosGrainStorage_Constructor_NullClusterOptions_ThrowsArgumentNullException()
    {
        var loggerFactory = new TrackingLoggerFactory();

        var exception = Assert.Throws<ArgumentNullException>(() => new CosmosGrainStorage(
            "storage",
            new CosmosGrainStorageOptions(),
            loggerFactory,
            _serviceProvider,
            null!,
            new TrackingDocumentIdProvider(),
            new ActivatorProvider()));

        Assert.Equal("clusterOptions", exception.ParamName);
        Assert.Equal(0, loggerFactory.CreateLoggerCallCount);
    }

    [Fact]
    public void CosmosGrainStorage_Constructor_NullOptionsAndClusterOptions_ValidatesOptionsFirst()
    {
        var loggerFactory = new TrackingLoggerFactory();

        var exception = Assert.Throws<ArgumentNullException>(() => new CosmosGrainStorage(
            "storage",
            null!,
            loggerFactory,
            _serviceProvider,
            null!,
            new TrackingDocumentIdProvider(),
            new ActivatorProvider()));

        Assert.Equal("options", exception.ParamName);
        Assert.Equal(0, loggerFactory.CreateLoggerCallCount);
    }

    [Theory]
    [InlineData(StorageOperation.Read)]
    [InlineData(StorageOperation.Write)]
    [InlineData(StorageOperation.Clear)]
    public async Task CosmosGrainStorage_Operation_NullGrainState_ThrowsBeforeResolvingDocumentIdentifiers(
        StorageOperation operation)
    {
        var documentIdProvider = new TrackingDocumentIdProvider();
        var storage = CreateStorage(documentIdProvider);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => InvokeStorageOperation(storage, operation));

        Assert.Equal("grainState", exception.ParamName);
        Assert.Equal(0, documentIdProvider.CallCount);
    }

    [Fact]
    public void DefaultDocumentIdProvider_Constructor_NullOptions_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new DefaultDocumentIdProvider(null!));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void DefaultDocumentIdProvider_GetPartitionKey_NullGrainType_ThrowsArgumentNullException()
    {
        var provider = new DefaultDocumentIdProvider(CreateClusterOptions());
        var grainId = GrainId.Create("grain/type", "grain/key");

        var exception = Assert.Throws<ArgumentNullException>(
            () => provider.GetPartitionKey(null!, grainId));

        Assert.Equal("grainType", exception.ParamName);
    }

    [Fact]
    public async Task DefaultDocumentIdProvider_ValidIdentifiers_PreserveFormatting()
    {
        var provider = new DefaultDocumentIdProvider(
            Options.Create(new ClusterOptions { ServiceId = "service/id" }));
        var grainId = GrainId.Create("grain/type", "grain/key");

        var identifiers = await provider.GetDocumentIdentifiers("partition/type", grainId);
        var documentId = provider.GetId("partition/type", grainId);
        var partitionKey = provider.GetPartitionKey("partition/type", grainId);

        Assert.Equal("service~0id__grain~0type_grain~0key", identifiers.DocumentId);
        Assert.Equal("service~0id__grain~0type_grain~0key", documentId);
        Assert.Equal("partition~0type", identifiers.PartitionKey);
        Assert.Equal("partition~0type", partitionKey);
    }

    public void Dispose() => _serviceProvider.Dispose();

    private CosmosGrainStorage CreateStorage(IDocumentIdProvider documentIdProvider) => new(
        "storage",
        new CosmosGrainStorageOptions(),
        NullLoggerFactory.Instance,
        _serviceProvider,
        CreateClusterOptions(),
        documentIdProvider,
        new ActivatorProvider());

    private static IOptions<ClusterOptions> CreateClusterOptions() =>
        Options.Create(new ClusterOptions { ServiceId = "service" });

    private static Task InvokeStorageOperation(CosmosGrainStorage storage, StorageOperation operation)
    {
        var grainId = GrainId.Create("grain/type", "grain/key");
        return operation switch
        {
            StorageOperation.Read => storage.ReadStateAsync<TestState>("grain-type", grainId, null!),
            StorageOperation.Write => storage.WriteStateAsync<TestState>("grain-type", grainId, null!),
            StorageOperation.Clear => storage.ClearStateAsync<TestState>("grain-type", grainId, null!),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    public enum StorageOperation
    {
        Read,
        Write,
        Clear,
    }

    private sealed class TrackingDocumentIdProvider : IDocumentIdProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<(string DocumentId, string PartitionKey)> GetDocumentIdentifiers(
            string grainType,
            GrainId grainId)
        {
            CallCount++;
            return new(("document", "partition"));
        }
    }

    private sealed class TrackingLoggerFactory : ILoggerFactory
    {
        public int CreateLoggerCallCount { get; private set; }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
        {
            CreateLoggerCallCount++;
            return NullLogger.Instance;
        }

        public void Dispose()
        {
        }
    }

    private sealed class ActivatorProvider : IActivatorProvider
    {
        public IActivator<T> GetActivator<T>() => new DefaultActivator<T>();
    }

    private sealed class DefaultActivator<T> : IActivator<T>
    {
        public T Create() => Activator.CreateInstance<T>();
    }

    private sealed class TestState;
}
