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
using Xunit;

namespace Tester.AzureUtils.Persistence;

[TestCategory("Persistence"), TestCategory("AzureStorage")]
public sealed class AzureBlobGrainStorageTests : AzureStorageBasicTests, IAsyncDisposable
{
    private const string GrainType = "test-grain";
    private readonly BlobContainerClient _container;
    private readonly string _containerName = $"test-grainstate-{Guid.NewGuid():N}";
    private readonly GrainId _grainId = GrainId.Create(GrainType, Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;

    public AzureBlobGrainStorageTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSerializer();
        _services = services.BuildServiceProvider();

        var options = new AzureBlobStorageOptions();
        options.ConfigureTestDefaults();
        _container = options.BlobServiceClient.GetBlobContainerClient(_containerName);
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DeleteIfExistsAsync();
        _services.Dispose();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task AzureBlobStorage_ReadState_StreamDeserializationFailure_DoesNotMutateGrainState()
    {
        await AssertFailedReadDoesNotMutateStateAsync(
            new ThrowingStreamDeserializeSerializer(CreateSetupSerializer()),
            usePooledBufferForReads: true);
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task AzureBlobStorage_ReadState_PooledBinaryDeserializationFailure_DoesNotMutateGrainState()
    {
        await AssertFailedReadDoesNotMutateStateAsync(
            new ThrowingBinaryDeserializeSerializer(CreateSetupSerializer()),
            usePooledBufferForReads: true);
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task AzureBlobStorage_ReadState_BinaryDeserializationFailure_DoesNotMutateGrainState()
    {
        await AssertFailedReadDoesNotMutateStateAsync(
            new ThrowingBinaryDeserializeSerializer(CreateSetupSerializer()),
            usePooledBufferForReads: false);
    }

    private async Task AssertFailedReadDoesNotMutateStateAsync(IGrainStorageSerializer serializer, bool usePooledBufferForReads)
    {
        var storage = await CreateStorageAsync(serializer, usePooledBufferForReads);
        var blob = _container.GetBlobClient(GetBlobName());
        await blob.UploadAsync(CreateSetupSerializer().Serialize(new TestState { Value = 7 }), overwrite: true);

        var actualEtag = (await blob.GetPropertiesAsync()).Value.ETag.ToString();
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

    private async Task<AzureBlobGrainStorage> CreateStorageAsync(IGrainStorageSerializer serializer, bool usePooledBufferForReads)
    {
        var options = new AzureBlobStorageOptions
        {
            ContainerName = _containerName,
            GrainStorageSerializer = serializer,
            UsePooledBufferForReads = usePooledBufferForReads,
        }.ConfigureTestDefaults();

        var activatorProvider = _services.GetRequiredService<IActivatorProvider>();
        var containerFactory = options.BuildContainerFactory(_services, options);
        await containerFactory.InitializeAsync(options.BlobServiceClient);

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
        public BinaryData Serialize<T>(T input) => inner.Serialize(input);

        public T Deserialize<T>(BinaryData input) => throw new InvalidOperationException("Binary deserialization failed.");
    }

    private sealed class ThrowingStreamDeserializeSerializer(IGrainStorageSerializer inner) : IGrainStorageSerializer, IGrainStorageStreamingSerializer
    {
        private readonly IGrainStorageStreamingSerializer _innerStream = inner as IGrainStorageStreamingSerializer
            ?? throw new InvalidOperationException("The inner serializer must support streaming.");

        public BinaryData Serialize<T>(T input) => inner.Serialize(input);

        public T Deserialize<T>(BinaryData input) => inner.Deserialize<T>(input);

        public ValueTask SerializeAsync<T>(T input, Stream destination, CancellationToken cancellationToken = default)
            => _innerStream.SerializeAsync(input, destination, cancellationToken);

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
