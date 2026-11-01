#nullable enable

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
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

    [Fact, TestCategory("Functional")]
    public async Task WriteStateAsync_ShouldSucceed_WhenRetryMatchesProviderTokenAndContentHash_WhenIdempotentWritesOptionIsEnabled()
    {
        var serializer = CreateSetupSerializer();
        var storage = await CreateStorageAsync(serializer, enableIdempotentWrites: true);
        var grainState = new GrainState<TestState> { State = new TestState { Value = 42 } };
        var blob = _container.GetBlobClient(GetBlobName());

        await storage.WriteStateAsync(GrainType, _grainId, grainState);

        var initialETag = Assert.IsType<string>(grainState.ETag);
        var contents = serializer.Serialize(grainState.State);
        var expectedHash = ComputeHashBase64(contents);
        var properties = await blob.GetPropertiesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(properties.Value.Metadata.TryGetValue("OrleansUpdateToken", out var updateToken));
        Assert.True(properties.Value.Metadata.TryGetValue("ContentSha256", out var storedHash));
        Assert.Equal(expectedHash, storedHash);

        var remoteWrite = await blob.UploadAsync(
            contents,
            new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfMatch = new ETag(initialETag) },
                Metadata = new Dictionary<string, string>
                {
                    ["OrleansUpdateToken"] = updateToken,
                    ["ContentSha256"] = expectedHash,
                },
            },
            TestContext.Current.CancellationToken);

        await storage.WriteStateAsync(GrainType, _grainId, grainState);

        Assert.Equal(remoteWrite.Value.ETag.ToString(), grainState.ETag);
        Assert.True(grainState.RecordExists);
    }

    [Fact, TestCategory("Functional")]
    public async Task WriteStateAsync_ShouldThrowInconsistentState_WhenRetryTokenBelongsToAnotherProvider_WhenIdempotentWritesOptionIsEnabled()
    {
        var serializer = CreateSetupSerializer();
        var firstStorage = await CreateStorageAsync(serializer, enableIdempotentWrites: true);
        var secondStorage = await CreateStorageAsync(serializer, enableIdempotentWrites: true);
        var firstState = new GrainState<TestState> { State = new TestState { Value = 42 } };
        var blob = _container.GetBlobClient(GetBlobName());

        await firstStorage.WriteStateAsync(GrainType, _grainId, firstState);

        var staleETag = Assert.IsType<string>(firstState.ETag);
        var firstProperties = await blob.GetPropertiesAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(firstProperties.Value.Metadata.TryGetValue("OrleansUpdateToken", out var firstToken));

        var secondState = new GrainState<TestState>
        {
            ETag = staleETag,
            RecordExists = true,
            State = new TestState { Value = 42 },
        };
        await secondStorage.WriteStateAsync(GrainType, _grainId, secondState);

        var secondProperties = await blob.GetPropertiesAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(secondProperties.Value.Metadata.TryGetValue("OrleansUpdateToken", out var secondToken));
        Assert.NotEqual(firstToken, secondToken);

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => firstStorage.WriteStateAsync(GrainType, _grainId, firstState));

        Assert.Contains("Blob storage condition not Satisfied", exception.Message);
    }

    [Fact, TestCategory("Functional")]
    public async Task WriteStateAsync_ShouldHashStreamSerializedContent_WhenIdempotentWritesOptionIsEnabled()
    {
        var serializer = new StreamingOnlyGrainStorageSerializer();
        var storage = await CreateStorageAsync(serializer, enableIdempotentWrites: true);
        var grainState = new GrainState<TestState> { State = new TestState { Value = 42 } };
        var blob = _container.GetBlobClient(GetBlobName());

        await storage.WriteStateAsync(GrainType, _grainId, grainState);

        var properties = await blob.GetPropertiesAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(properties.Value.Metadata.TryGetValue("ContentSha256", out var storedHash));
        Assert.Equal(
            Convert.ToBase64String(SHA256.HashData(StreamingOnlyGrainStorageSerializer.SerializedContent.Span)),
            storedHash);
    }

    [Fact, TestCategory("Functional")]
    public async Task WriteStateAsync_ShouldThrowInconsistentState_WhenProviderTokenMatchesButContentHashDiffers_WhenIdempotentWritesOptionIsEnabled()
    {
        var serializer = CreateSetupSerializer();
        var storage = await CreateStorageAsync(serializer, enableIdempotentWrites: true);
        var grainState = new GrainState<TestState> { State = new TestState { Value = 42 } };
        var blob = _container.GetBlobClient(GetBlobName());

        await storage.WriteStateAsync(GrainType, _grainId, grainState);

        var initialETag = Assert.IsType<string>(grainState.ETag);
        var originalContents = serializer.Serialize(grainState.State);
        var originalHash = ComputeHashBase64(originalContents);
        var properties = await blob.GetPropertiesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(properties.Value.Metadata.TryGetValue("OrleansUpdateToken", out var updateToken));
        Assert.True(properties.Value.Metadata.TryGetValue("ContentSha256", out var storedHash));
        Assert.Equal(originalHash, storedHash);

        await blob.UploadAsync(
            originalContents,
            new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfMatch = new ETag(initialETag) },
                Metadata = new Dictionary<string, string>
                {
                    ["OrleansUpdateToken"] = updateToken,
                    ["ContentSha256"] = originalHash,
                },
            },
            TestContext.Current.CancellationToken);

        grainState.State = new TestState { Value = 99 };
        grainState.ETag = initialETag;

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.WriteStateAsync(GrainType, _grainId, grainState));

        Assert.Contains("Blob storage condition not Satisfied", exception.Message);
    }

    [Fact, TestCategory("Functional")]
    public async Task WriteStateAsync_ShouldThrowInconsistentState_WhenIdempotentWritesOptionIsDisabled()
    {
        var serializer = CreateSetupSerializer();
        var storage = await CreateStorageAsync(serializer);
        var grainState = new GrainState<TestState> { State = new TestState { Value = 42 } };
        var blob = _container.GetBlobClient(GetBlobName());

        await storage.WriteStateAsync(GrainType, _grainId, grainState);

        var initialETag = Assert.IsType<string>(grainState.ETag);
        var originalContents = serializer.Serialize(grainState.State);
        var originalHash = ComputeHashBase64(originalContents);

        await blob.UploadAsync(
            originalContents,
            new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfMatch = new ETag(initialETag) },
                Metadata = new Dictionary<string, string>
                {
                    ["OrleansUpdateToken"] = Guid.NewGuid().ToString(),
                    ["ContentSha256"] = originalHash,
                },
            },
            TestContext.Current.CancellationToken);

        grainState.State = new TestState { Value = 99 };
        grainState.ETag = initialETag;

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.WriteStateAsync(GrainType, _grainId, grainState));

        Assert.Contains("Blob storage condition not Satisfied", exception.Message);
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

    private async Task<AzureBlobGrainStorage> CreateStorageAsync(
        IGrainStorageSerializer serializer,
        bool deleteStateOnClear = true,
        bool? enableIdempotentWrites = null)
    {
        var options = new AzureBlobStorageOptions
        {
            ContainerName = _containerName,
            GrainStorageSerializer = serializer,
            DeleteStateOnClear = deleteStateOnClear,
        }.ConfigureTestDefaults();
        if (enableIdempotentWrites is { } enable)
        {
            options.EnableIdempotentWrites = enable;
        }

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

    private static string ComputeHashBase64(BinaryData contents)
        => Convert.ToBase64String(SHA256.HashData(contents.ToArray()));

    private string GetBlobName() => $"{GrainType}-{_grainId}.json";

    private sealed class StreamingOnlyGrainStorageSerializer : IGrainStorageStreamingSerializer
    {
        public static ReadOnlyMemory<byte> SerializedContent { get; } = Encoding.UTF8.GetBytes("stream-only-payload");

        public BinaryData Serialize<T>(T? input)
            => throw new InvalidOperationException("The binary serializer should not be used.");

        public T? Deserialize<T>(BinaryData input) => default;

        public ValueTask SerializeAsync<T>(T? input, Stream destination, CancellationToken cancellationToken = default)
            => destination.WriteAsync(SerializedContent, cancellationToken);

        public ValueTask<T?> DeserializeAsync<T>(Stream input, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<T?>(default);
    }

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
