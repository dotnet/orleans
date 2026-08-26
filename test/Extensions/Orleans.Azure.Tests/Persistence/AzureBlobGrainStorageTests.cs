#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using Tester.AzureUtils;
using TestExtensions;
using Orleans.Persistence.TestKit;
using Xunit;

namespace Tester.AzureUtils.Persistence;

[TestCategory("Persistence"), TestCategory("AzureStorage")]
[TestSuite("Functional")]
[TestProvider("AzureStorage")]
[TestArea("Persistence")]
public sealed class AzureBlobGrainStorageTests : AzureStorageBasicTests, IAsyncDisposable
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);
    private const string GrainType = "test-grain";
    private readonly BlobContainerClient _container;
    private readonly string _containerName = $"test-grainstate-{Guid.NewGuid():N}";
    private readonly GrainId _grainId = GrainId.Create(GrainType, Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private readonly ServiceProvider _services;

    public AzureBlobGrainStorageTests(ITestOutputHelper output)
    {
        _output = output;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSerializer();
        _services = services.BuildServiceProvider();

        var options = new AzureBlobStorageOptions();
        options.ConfigureTestDefaults();
        var blobServiceClient = options.BlobServiceClient;
        Assert.NotNull(blobServiceClient);
        _container = blobServiceClient.GetBlobContainerClient(_containerName);
    }

    public async ValueTask DisposeAsync()
    {
        using var cleanupCancellation = new CancellationTokenSource(CleanupTimeout);
        try
        {
            await _container.DeleteIfExistsAsync(
                cancellationToken: cleanupCancellation.Token);
        }
        finally
        {
            _services.Dispose();
        }
    }

    [Fact, TestCategory("Functional")]
    public async Task AzureBlobStorage_ReadState_StreamDeserializationFailure_DoesNotMutateGrainState()
    {
        await AssertFailedReadDoesNotMutateStateAsync(
            new ThrowingStreamDeserializeSerializer(CreateSetupSerializer()),
            TestContext.Current.CancellationToken);
    }

    [Fact, TestCategory("Functional")]
    public async Task AzureBlobStorage_ReadState_PooledBinaryDeserializationFailure_DoesNotMutateGrainState()
    {
        await AssertFailedReadDoesNotMutateStateAsync(
            new ThrowingBinaryDeserializeSerializer(CreateSetupSerializer()),
            TestContext.Current.CancellationToken);
    }

    [Fact, TestCategory("Functional"), TestCategory("ModelBased")]
    public async Task AzureBlobStorage_ModelBasedGeneratedConformance()
    {
        var storage = await CreateStorageAsync(CreateSetupSerializer());
        var runner = new GrainStorageModelBasedTestRunner(storage, "AzureBlob", _output.WriteLine);

        await runner.RunGeneratedConformanceTests(TestContext.Current.CancellationToken);
    }

    [Fact, TestCategory("Functional"), TestCategory("ModelBased")]
    public async Task AzureBlobStorage_ClearWritesTombstone_ModelBasedGeneratedConformance()
    {
        var storage = await CreateStorageAsync(CreateSetupSerializer(), deleteStateOnClear: false);
        var runner = new GrainStorageModelBasedTestRunner(storage, "AzureBlobClearWritesTombstone", _output.WriteLine);

        await runner.RunGeneratedConformanceTests(TestContext.Current.CancellationToken);
    }

    private async Task AssertFailedReadDoesNotMutateStateAsync(
        IGrainStorageSerializer serializer,
        CancellationToken cancellationToken)
    {
        var storage = await CreateStorageAsync(serializer);
        var blob = _container.GetBlobClient(GetBlobName());
        await blob.UploadAsync(
            CreateSetupSerializer().Serialize(new TestState { Value = 7 }),
            overwrite: true,
            cancellationToken: cancellationToken);

        var actualEtag = (await blob.GetPropertiesAsync(
            cancellationToken: cancellationToken)).Value.ETag.ToString();
        const string initialEtag = "\"initial-etag\"";
        Assert.NotEqual(initialEtag, actualEtag);

        var grainState = new GrainState<TestState>
        {
            ETag = initialEtag,
            RecordExists = true,
            State = new TestState { Value = 123 }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.ReadStateAsync(GrainType, _grainId, grainState));

        Assert.Equal(initialEtag, grainState.ETag);
        Assert.True(grainState.RecordExists);
        Assert.Equal(123, grainState.State.Value);
    }

    private async Task<AzureBlobGrainStorage> CreateStorageAsync(IGrainStorageSerializer serializer, bool deleteStateOnClear = true)
    {
        var options = new AzureBlobStorageOptions
        {
            ContainerName = _containerName,
            GrainStorageSerializer = serializer,
            DeleteStateOnClear = deleteStateOnClear,
        }.ConfigureTestDefaults();

        var activatorProvider = _services.GetRequiredService<IActivatorProvider>();
        var containerFactory = options.BuildContainerFactory(_services, options);
        var blobServiceClient = options.BlobServiceClient;
        Assert.NotNull(blobServiceClient);
        await containerFactory.InitializeAsync(blobServiceClient);

        return new AzureBlobGrainStorage(
            "AzureStore",
            options,
            containerFactory,
            activatorProvider,
            NullLogger<AzureBlobGrainStorage>.Instance);
    }

    private static IGrainStorageSerializer CreateSetupSerializer()
        => new JsonGrainStorageSerializer(new OrleansJsonSerializer(Options.Create(new OrleansJsonSerializerOptions())));

    private string GetBlobName() => $"{GrainType}-{_grainId}.json";

    private sealed class ThrowingBinaryDeserializeSerializer(IGrainStorageSerializer inner) : IGrainStorageSerializer
    {
        public BinaryData Serialize<T>(T? input) => inner.Serialize(input);

        public T? Deserialize<T>(BinaryData input) => throw new InvalidOperationException("Binary deserialization failed.");
    }

    private sealed class ThrowingStreamDeserializeSerializer(IGrainStorageSerializer inner) : IGrainStorageStreamingSerializer
    {
        private readonly IGrainStorageStreamingSerializer _inner = inner as IGrainStorageStreamingSerializer
            ?? throw new InvalidOperationException("The inner serializer must support streaming.");

        public BinaryData Serialize<T>(T? input) => _inner.Serialize(input);

        public T? Deserialize<T>(BinaryData input) => _inner.Deserialize<T>(input);

        public ValueTask SerializeAsync<T>(T? input, Stream destination, CancellationToken cancellationToken = default)
            => _inner.SerializeAsync(input, destination, cancellationToken);

        public ValueTask<T?> DeserializeAsync<T>(Stream input, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Stream deserialization failed.");
    }

    [GenerateSerializer]
    internal sealed class TestState
    {
        [Id(0)]
        public int Value { get; set; }
    }
}
