#nullable enable

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.Persistence;

[TestCategory("Persistence"), TestCategory("AzureStorage"), TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("AzureStorage")]
[TestArea("Persistence")]
public sealed class AzureGrainStorageServiceIndependentTests : IDisposable
{
    private const string GrainType = "service-independent-grain";
    private static readonly GrainId TestGrainId = GrainId.Create(GrainType, "grain-1");
    private readonly ServiceProvider _services;
    private readonly CountingGrainStorageSerializer _serializer;
    private readonly CountingActivatorProvider _activatorProvider;

    public AzureGrainStorageServiceIndependentTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSerializer();
        _services = services.BuildServiceProvider();

        _serializer = new CountingGrainStorageSerializer(
            new OrleansGrainStorageSerializer(_services.GetRequiredService<Serializer>()));
        _activatorProvider = new CountingActivatorProvider(
            _services.GetRequiredService<IActivatorProvider>());
    }

    [Fact]
    public void BlobConstructor_NullOptions_ThrowsArgumentNullException()
    {
        var factory = new CountingBlobContainerFactory();
        AzureBlobGrainStorage? storage = null;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            storage = new AzureBlobGrainStorage(
                "AzureBlob",
                null!,
                factory,
                _activatorProvider,
                NullLogger<AzureBlobGrainStorage>.Instance));

        Assert.Equal("options", exception.ParamName);
        Assert.Null(storage);
        AssertBlobFactoryUntouched(factory);
        AssertSharedCollaboratorsUntouched();
    }

    [Fact]
    public async Task BlobReadState_NullGrainState_ThrowsBeforeStorageAccess()
    {
        var factory = new CountingBlobContainerFactory();
        IGrainStorage storage = CreateBlobStorage(factory);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            storage.ReadStateAsync<TestState>(
                GrainType,
                TestGrainId,
                null!,
                TestContext.Current.CancellationToken));

        Assert.Equal("grainState", exception.ParamName);
        AssertBlobFactoryUntouched(factory);
        AssertSharedCollaboratorsUntouched();
    }

    [Fact]
    public async Task BlobWriteState_NullGrainState_ThrowsBeforeStorageAccess()
    {
        var factory = new CountingBlobContainerFactory();
        IGrainStorage storage = CreateBlobStorage(factory);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            storage.WriteStateAsync<TestState>(
                GrainType,
                TestGrainId,
                null!,
                TestContext.Current.CancellationToken));

        Assert.Equal("grainState", exception.ParamName);
        AssertBlobFactoryUntouched(factory);
        AssertSharedCollaboratorsUntouched();
    }

    [Fact]
    public async Task BlobClearState_NullGrainState_ThrowsBeforeStorageAccess()
    {
        var factory = new CountingBlobContainerFactory();
        IGrainStorage storage = CreateBlobStorage(factory);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            storage.ClearStateAsync<TestState>(
                GrainType,
                TestGrainId,
                null!,
                TestContext.Current.CancellationToken));

        Assert.Equal("grainState", exception.ParamName);
        AssertBlobFactoryUntouched(factory);
        AssertSharedCollaboratorsUntouched();
    }

    [Theory]
    [InlineData(StorageOperation.Read)]
    [InlineData(StorageOperation.Write)]
    [InlineData(StorageOperation.Clear)]
    public async Task BlobOperation_NullGrainType_ThrowsBeforeStorageAccess(StorageOperation operation)
    {
        var factory = new CountingBlobContainerFactory();
        IGrainStorage storage = CreateBlobStorage(factory);
        var value = new TestState();
        var grainState = new GrainState<TestState>(value)
        {
            ETag = "original-etag",
            RecordExists = true,
        };

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => InvokeStorageOperation(storage, operation, null!, grainState));

        Assert.Equal("grainType", exception.ParamName);
        Assert.Same(value, grainState.State);
        Assert.Equal("original-etag", grainState.ETag);
        Assert.True(grainState.RecordExists);
        AssertBlobFactoryUntouched(factory);
        AssertSharedCollaboratorsUntouched();
    }

    [Fact]
    public void TableConstructor_NullOptions_ThrowsArgumentNullException()
    {
        AzureTableGrainStorage? storage = null;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            storage = new AzureTableGrainStorage(
                "AzureTable",
                null!,
                CreateClusterOptions(),
                NullLogger<AzureTableGrainStorage>.Instance,
                _activatorProvider));

        Assert.Equal("options", exception.ParamName);
        Assert.Null(storage);
        AssertSharedCollaboratorsUntouched();
    }

    [Fact]
    public void TableConstructor_NullClusterOptions_ThrowsArgumentNullException()
    {
        using var fixture = new CountingTableFixture(_serializer);
        var options = fixture.Options;
        var client = fixture.Client;
        AzureTableGrainStorage? storage = null;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            storage = new AzureTableGrainStorage(
                "AzureTable",
                options,
                null!,
                NullLogger<AzureTableGrainStorage>.Instance,
                _activatorProvider));

        Assert.Equal("clusterOptions", exception.ParamName);
        Assert.Null(storage);
        Assert.Same(options, fixture.Options);
        Assert.Same(client, options.TableServiceClient);
        Assert.Equal(0, fixture.Handler.RequestCount);
        AssertSharedCollaboratorsUntouched();
    }

    [Fact]
    public async Task TableReadState_NullGrainState_ThrowsBeforeStorageAccess()
    {
        using var fixture = new CountingTableFixture(_serializer);
        IGrainStorage storage = CreateTableStorage(fixture.Options);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            storage.ReadStateAsync<TestState>(
                GrainType,
                TestGrainId,
                null!,
                TestContext.Current.CancellationToken));

        Assert.Equal("grainState", exception.ParamName);
        Assert.Equal(0, fixture.Handler.RequestCount);
        AssertSharedCollaboratorsUntouched();
    }

    [Fact]
    public async Task TableWriteState_NullGrainState_ThrowsBeforeStorageAccess()
    {
        using var fixture = new CountingTableFixture(_serializer);
        IGrainStorage storage = CreateTableStorage(fixture.Options);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            storage.WriteStateAsync<TestState>(
                GrainType,
                TestGrainId,
                null!,
                TestContext.Current.CancellationToken));

        Assert.Equal("grainState", exception.ParamName);
        Assert.Equal(0, fixture.Handler.RequestCount);
        AssertSharedCollaboratorsUntouched();
    }

    [Fact]
    public async Task TableClearState_NullGrainState_ThrowsBeforeStorageAccess()
    {
        using var fixture = new CountingTableFixture(_serializer);
        IGrainStorage storage = CreateTableStorage(fixture.Options);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            storage.ClearStateAsync<TestState>(
                GrainType,
                TestGrainId,
                null!,
                TestContext.Current.CancellationToken));

        Assert.Equal("grainState", exception.ParamName);
        Assert.Equal(0, fixture.Handler.RequestCount);
        AssertSharedCollaboratorsUntouched();
    }

    [Theory]
    [InlineData(StorageOperation.Read)]
    [InlineData(StorageOperation.Write)]
    [InlineData(StorageOperation.Clear)]
    public async Task TableOperation_NullGrainType_ThrowsBeforeStorageAccess(StorageOperation operation)
    {
        using var fixture = new CountingTableFixture(_serializer);
        IGrainStorage storage = CreateTableStorage(fixture.Options);
        var value = new TestState();
        var grainState = new GrainState<TestState>(value)
        {
            ETag = "original-etag",
            RecordExists = true,
        };

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => InvokeStorageOperation(storage, operation, null!, grainState));

        Assert.Equal("grainType", exception.ParamName);
        Assert.Same(value, grainState.State);
        Assert.Equal("original-etag", grainState.ETag);
        Assert.True(grainState.RecordExists);
        Assert.Equal(0, fixture.Handler.RequestCount);
        AssertSharedCollaboratorsUntouched();
    }

    public void Dispose() => _services.Dispose();

    private AzureBlobGrainStorage CreateBlobStorage(IBlobContainerFactory factory)
        => new(
            "AzureBlob",
            new AzureBlobStorageOptions { GrainStorageSerializer = _serializer },
            factory,
            _activatorProvider,
            NullLogger<AzureBlobGrainStorage>.Instance);

    private AzureTableGrainStorage CreateTableStorage(AzureTableStorageOptions options)
        => new(
            "AzureTable",
            options,
            CreateClusterOptions(),
            NullLogger<AzureTableGrainStorage>.Instance,
            _activatorProvider);

    private static IOptions<ClusterOptions> CreateClusterOptions()
        => Options.Create(new ClusterOptions
        {
            ServiceId = "service-independent-service",
            ClusterId = "service-independent-cluster",
        });

    private static Task InvokeStorageOperation<T>(
        IGrainStorage storage,
        StorageOperation operation,
        string grainType,
        IGrainState<T> grainState)
        => operation switch
        {
            StorageOperation.Read => storage.ReadStateAsync(
                grainType,
                TestGrainId,
                grainState,
                TestContext.Current.CancellationToken),
            StorageOperation.Write => storage.WriteStateAsync(
                grainType,
                TestGrainId,
                grainState,
                TestContext.Current.CancellationToken),
            StorageOperation.Clear => storage.ClearStateAsync(
                grainType,
                TestGrainId,
                grainState,
                TestContext.Current.CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static void AssertBlobFactoryUntouched(CountingBlobContainerFactory factory)
    {
        Assert.Equal(0, factory.InitializeCallCount);
        Assert.Equal(0, factory.GetClientCallCount);
    }

    private void AssertSharedCollaboratorsUntouched()
    {
        Assert.Equal(0, _serializer.SerializeCallCount);
        Assert.Equal(0, _serializer.DeserializeCallCount);
        Assert.Equal(0, _activatorProvider.GetActivatorCallCount);
    }

    private sealed class CountingBlobContainerFactory : IBlobContainerFactory
    {
        public int InitializeCallCount { get; private set; }

        public int GetClientCallCount { get; private set; }

        public BlobContainerClient GetBlobContainerClient(GrainId grainId)
        {
            GetClientCallCount++;
            throw new InvalidOperationException("Blob container access is not expected.");
        }

        public Task InitializeAsync(BlobServiceClient client)
        {
            InitializeCallCount++;
            throw new InvalidOperationException("Blob factory initialization is not expected.");
        }
    }

    private sealed class CountingGrainStorageSerializer(IGrainStorageSerializer inner)
        : IGrainStorageSerializer
    {
        public int SerializeCallCount { get; private set; }

        public int DeserializeCallCount { get; private set; }

        public BinaryData Serialize<T>(T? input)
        {
            SerializeCallCount++;
            return inner.Serialize(input);
        }

        public T? Deserialize<T>(BinaryData input)
        {
            DeserializeCallCount++;
            return inner.Deserialize<T>(input);
        }
    }

    private sealed class CountingActivatorProvider(IActivatorProvider inner) : IActivatorProvider
    {
        public int GetActivatorCallCount { get; private set; }

        public IActivator<T> GetActivator<T>()
        {
            GetActivatorCallCount++;
            return inner.GetActivator<T>();
        }
    }

    private sealed class CountingTableFixture : IDisposable
    {
        private readonly HttpClient _httpClient;

        public CountingTableFixture(IGrainStorageSerializer serializer)
        {
            Handler = new CountingHttpMessageHandler();
            _httpClient = new HttpClient(Handler);
            var clientOptions = new TableClientOptions
            {
                Transport = new HttpClientTransport(_httpClient),
            };

            Client = new TableServiceClient(
                new Uri("https://unit.test"),
                new TableSharedKeyCredential(
                    "unitaccount",
                    Convert.ToBase64String(new byte[32])),
                clientOptions);
            Options = new AzureTableStorageOptions
            {
                GrainStorageSerializer = serializer,
                TableServiceClient = Client,
            };
        }

        public CountingHttpMessageHandler Handler { get; }

        public TableServiceClient Client { get; }

        public AzureTableStorageOptions Options { get; }

        public void Dispose() => _httpClient.Dispose();
    }

    private sealed class CountingHttpMessageHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            throw new InvalidOperationException("Table service access is not expected.");
        }
    }

    public enum StorageOperation
    {
        Read,
        Write,
        Clear,
    }

    private sealed class TestState;
}
