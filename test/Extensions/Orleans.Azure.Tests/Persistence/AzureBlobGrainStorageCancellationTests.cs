#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.Persistence;

[TestCategory("BVT"), TestCategory("Persistence"), TestCategory("AzureStorage")]
[TestSuite("Unit")]
[TestProvider("AzureStorage")]
[TestArea("Persistence")]
public sealed class AzureBlobGrainStorageCancellationTests : IDisposable
{
    private const string GrainType = "test-grain";
    private const string InitialETag = "\"initial-etag\"";
    private const string StoredETag = "\"stored-etag\"";
    private static readonly GrainId TestGrainId = GrainId.Create(GrainType, "cancellation");
    private readonly ServiceProvider _services = new ServiceCollection().AddSerializer().BuildServiceProvider();
    private readonly TestBlobClient _blob = new();
    private readonly TestLogger _logger = new();

    public void Dispose() => _services.Dispose();

    [Theory]
    [InlineData(Operation.Read)]
    [InlineData(Operation.Write)]
    [InlineData(Operation.Delete)]
    [InlineData(Operation.Overwrite)]
    public async Task Operations_ForwardCallerToken_AndUpdateStateOnSuccess(Operation operation)
    {
        using var cancellation = new CancellationTokenSource();
        var storage = CreateStorage(operation, new BinarySerializer(), out _);
        var state = CreateState();
        var original = state.State;

        await RunAsync(storage, state, operation, cancellation.Token);

        Assert.Equal(cancellation.Token, Assert.Single(_blob.RequestTokens));
        AssertSuccessfulState(state, original, operation);
        Assert.Empty(_logger.Errors);
        if (operation != Operation.Read)
        {
            Assert.Equal(new ETag(InitialETag), _blob.Conditions!.IfMatch);
        }

        if (operation == Operation.Overwrite)
        {
            Assert.Empty(_blob.UploadedContent!.ToArray());
        }
    }

    [Theory]
    [InlineData(Operation.Read)]
    [InlineData(Operation.Write)]
    [InlineData(Operation.Delete)]
    [InlineData(Operation.Overwrite)]
    public async Task LegacyOverloads_ForwardNone_AndUpdateStateOnSuccess(Operation operation)
    {
        var storage = CreateStorage(operation, new BinarySerializer(), out _);
        var state = CreateState();
        var original = state.State;

        await RunAsync(storage, state, operation);

        Assert.Equal(CancellationToken.None, Assert.Single(_blob.RequestTokens));
        AssertSuccessfulState(state, original, operation);
    }

    [Theory]
    [InlineData(Operation.Read, false)]
    [InlineData(Operation.Write, false)]
    [InlineData(Operation.Delete, false)]
    [InlineData(Operation.Overwrite, false)]
    [InlineData(Operation.Read, true)]
    [InlineData(Operation.Write, true)]
    public async Task PreCanceledOperations_PreserveState_WithoutAccessingStorageOrSerializer(Operation operation, bool streaming)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var serializer = streaming ? new StreamingSerializer() : new BinarySerializer();
        var storage = CreateStorage(operation, serializer, out var factory);
        var state = CreateState();
        var original = state.State;

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunAsync(storage, state, operation, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        AssertUnchanged(state, original);
        Assert.Equal(0, factory.AccessCount);
        Assert.Equal(0, serializer.CallCount);
        Assert.Empty(_blob.RequestTokens);
        Assert.Empty(_logger.Errors);
    }

    [Theory]
    [InlineData(Operation.Read, false)]
    [InlineData(Operation.Write, false)]
    [InlineData(Operation.Delete, false)]
    [InlineData(Operation.Overwrite, false)]
    [InlineData(Operation.Read, true)]
    [InlineData(Operation.Write, true)]
    public async Task InFlightSdkCancellation_PropagatesOriginalException_AndPreservesState(Operation operation, bool streaming)
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new OperationCanceledException(cancellation.Token);
        _blob.OnRequest = token => AwaitCancellationAsync(token, cancellation.Token, started, expected);
        var serializer = streaming ? new StreamingSerializer() : new BinarySerializer();
        var storage = CreateStorage(operation, serializer, out _);
        var state = CreateState();
        var original = state.State;

        var pending = RunAsync(storage, state, operation, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Same(expected, actual);
        Assert.Equal(cancellation.Token, Assert.Single(_blob.RequestTokens));
        AssertUnchanged(state, original);
        Assert.Empty(_logger.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingSerializers_ForwardCallerToken(bool read)
    {
        using var cancellation = new CancellationTokenSource();
        var serializer = new StreamingSerializer();
        var operation = read ? Operation.Read : Operation.Write;
        var storage = CreateStorage(operation, serializer, out _);
        var state = CreateState();
        var original = state.State;

        await RunAsync(storage, state, operation, cancellation.Token);

        Assert.Equal(cancellation.Token, Assert.Single(serializer.Tokens));
        Assert.Equal(cancellation.Token, Assert.Single(_blob.RequestTokens));
        AssertSuccessfulState(state, original, operation);
        if (!read)
        {
            Assert.Equal(123, _blob.UploadedContent!.ToObjectFromJson<TestState>()!.Value);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingSerializerCancellation_PropagatesOriginalException_AndPreservesState(bool read)
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new OperationCanceledException(cancellation.Token);
        var serializer = new StreamingSerializer
        {
            OnOperation = token => AwaitCancellationAsync(token, cancellation.Token, started, expected),
        };
        var operation = read ? Operation.Read : Operation.Write;
        var storage = CreateStorage(operation, serializer, out _);
        var state = CreateState();
        var original = state.State;

        var pending = RunAsync(storage, state, operation, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Same(expected, actual);
        Assert.Equal(cancellation.Token, Assert.Single(serializer.Tokens));
        Assert.Equal(read ? 1 : 0, _blob.RequestTokens.Count);
        AssertUnchanged(state, original);
        Assert.Empty(_logger.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContentReads_ForwardCallerToken_InPooledAndLargePayloadPaths(bool largePayload)
    {
        using var cancellation = new CancellationTokenSource();
        using var content = new TokenTrackingStream();
        _blob.Content = content;
        _blob.ContentLength = largePayload ? (long)int.MaxValue + 1 : content.Length;
        var storage = CreateStorage(Operation.Read, new BinarySerializer(), out _);
        var state = CreateState();

        await storage.ReadStateAsync(GrainType, TestGrainId, state, cancellation.Token);

        Assert.NotEmpty(content.Tokens);
        Assert.All(content.Tokens, token => Assert.Equal(cancellation.Token, token));
        Assert.True(content.Disposed);
        Assert.NotNull(state.State);
        Assert.Equal(7, state.State.Value);
        Assert.Equal(StoredETag, state.ETag);
        Assert.True(state.RecordExists);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContentReadCancellation_PropagatesOriginalException_AndPreservesState(bool largePayload)
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new OperationCanceledException(cancellation.Token);
        using var content = new TokenTrackingStream
        {
            OnRead = token => AwaitCancellationAsync(token, cancellation.Token, started, expected),
        };
        _blob.Content = content;
        _blob.ContentLength = largePayload ? (long)int.MaxValue + 1 : content.Length;
        var storage = CreateStorage(Operation.Read, new BinarySerializer(), out _);
        var state = CreateState();
        var original = state.State;

        var pending = storage.ReadStateAsync(GrainType, TestGrainId, state, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Same(expected, actual);
        Assert.All(content.Tokens, token => Assert.Equal(cancellation.Token, token));
        Assert.True(content.Disposed);
        AssertUnchanged(state, original);
        Assert.Empty(_logger.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContainerCreationAndRetry_ForwardCallerToken(bool streaming)
    {
        using var cancellation = new CancellationTokenSource();
        _blob.FailFirstUploadWithMissingContainer = true;
        var serializer = streaming ? new StreamingSerializer() : new BinarySerializer();
        var storage = CreateStorage(Operation.Write, serializer, out var factory);
        var state = CreateState();
        var original = state.State;

        await storage.WriteStateAsync(GrainType, TestGrainId, state, cancellation.Token);

        Assert.Equal(2, _blob.RequestTokens.Count);
        Assert.All(_blob.RequestTokens, token => Assert.Equal(cancellation.Token, token));
        Assert.Equal(cancellation.Token, Assert.Single(factory.Container.CreateTokens));
        if (serializer is StreamingSerializer streamSerializer)
        {
            Assert.Equal(2, streamSerializer.Tokens.Count);
            Assert.All(streamSerializer.Tokens, token => Assert.Equal(cancellation.Token, token));
        }

        AssertSuccessfulState(state, original, Operation.Write);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContainerCreationCancellation_PropagatesOriginalException_WithoutRetryOrStateMutation(bool streaming)
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new OperationCanceledException(cancellation.Token);
        _blob.FailFirstUploadWithMissingContainer = true;
        var serializer = streaming ? new StreamingSerializer() : new BinarySerializer();
        var storage = CreateStorage(Operation.Write, serializer, out var factory);
        factory.Container.OnCreate = token => AwaitCancellationAsync(token, cancellation.Token, started, expected);
        var state = CreateState();
        var original = state.State;

        var pending = storage.WriteStateAsync(GrainType, TestGrainId, state, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Same(expected, actual);
        Assert.Equal(cancellation.Token, Assert.Single(factory.Container.CreateTokens));
        Assert.Single(_blob.RequestTokens);
        AssertUnchanged(state, original);
        Assert.Empty(_logger.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeRetry_PreventsSecondUpload(bool streaming)
    {
        using var cancellation = new CancellationTokenSource();
        _blob.FailFirstUploadWithMissingContainer = true;
        var serializer = streaming ? new StreamingSerializer() : new BinarySerializer();
        var storage = CreateStorage(Operation.Write, serializer, out var factory);
        factory.Container.OnCreate = _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };
        var state = CreateState();
        var original = state.State;

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.WriteStateAsync(GrainType, TestGrainId, state, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(cancellation.Token, Assert.Single(_blob.RequestTokens));
        Assert.Equal(cancellation.Token, Assert.Single(factory.Container.CreateTokens));
        Assert.Equal(1, serializer.CallCount);
        AssertUnchanged(state, original);
        Assert.Empty(_logger.Errors);
    }

    [Theory]
    [InlineData(Operation.Write)]
    [InlineData(Operation.Delete)]
    [InlineData(Operation.Overwrite)]
    public async Task OptimisticConcurrencyFailure_PreservesState_AndReportsConflict(Operation operation)
    {
        using var cancellation = new CancellationTokenSource();
        var expected = new RequestFailedException(412, "Condition failed.", "ConditionNotMet", null);
        _blob.OnRequest = _ => Task.FromException(expected);
        var storage = CreateStorage(operation, new BinarySerializer(), out _);
        var state = CreateState();
        var original = state.State;

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => RunAsync(storage, state, operation, cancellation.Token));

        Assert.Same(expected, exception.InnerException);
        Assert.Equal(InitialETag, exception.CurrentEtag);
        Assert.Equal(cancellation.Token, Assert.Single(_blob.RequestTokens));
        AssertUnchanged(state, original);
        Assert.Same(exception, Assert.Single(_logger.Errors));
    }

    [Theory]
    [InlineData(Operation.Read)]
    [InlineData(Operation.Write)]
    [InlineData(Operation.Delete)]
    [InlineData(Operation.Overwrite)]
    public async Task SdkCancellationWithoutCallerCancellation_PreservesState_AndReportsError(Operation operation)
    {
        using var cancellation = new CancellationTokenSource();
        var expected = new OperationCanceledException("SDK cancellation");
        _blob.OnRequest = _ => Task.FromException(expected);
        var storage = CreateStorage(operation, new BinarySerializer(), out _);
        var state = CreateState();
        var original = state.State;

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunAsync(storage, state, operation, cancellation.Token));

        Assert.Same(expected, exception);
        AssertUnchanged(state, original);
        Assert.Same(expected, Assert.Single(_logger.Errors));
    }

    [Theory]
    [InlineData("BlobNotFound")]
    [InlineData("ContainerNotFound")]
    public async Task ReadMissingBlobOrContainer_ResetsState(string errorCode)
    {
        using var cancellation = new CancellationTokenSource();
        _blob.OnRequest = _ => Task.FromException(new RequestFailedException(404, "Missing.", errorCode, null));
        var storage = CreateStorage(Operation.Read, new BinarySerializer(), out _);
        var state = CreateState();
        var original = state.State;

        await storage.ReadStateAsync(GrainType, TestGrainId, state, cancellation.Token);

        Assert.Equal(cancellation.Token, Assert.Single(_blob.RequestTokens));
        Assert.NotNull(state.State);
        Assert.NotSame(original, state.State);
        Assert.Equal(0, state.State.Value);
        Assert.Null(state.ETag);
        Assert.False(state.RecordExists);
        Assert.Empty(_logger.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadEmptyBlob_PreservesStoredETag_AndDisposesContent(bool streaming)
    {
        using var cancellation = new CancellationTokenSource();
        using var content = new TokenTrackingStream();
        content.SetLength(0);
        _blob.Content = content;
        var serializer = streaming ? new StreamingSerializer() : new BinarySerializer();
        var storage = CreateStorage(Operation.Read, serializer, out _);
        var state = CreateState();
        var original = state.State;

        await storage.ReadStateAsync(GrainType, TestGrainId, state, cancellation.Token);

        Assert.Equal(cancellation.Token, Assert.Single(_blob.RequestTokens));
        Assert.NotNull(state.State);
        Assert.NotSame(original, state.State);
        Assert.Equal(0, state.State.Value);
        Assert.Equal(StoredETag, state.ETag);
        Assert.False(state.RecordExists);
        Assert.Equal(0, serializer.CallCount);
        Assert.True(content.Disposed);
    }

    [Theory]
    [InlineData(Operation.Write)]
    [InlineData(Operation.Delete)]
    [InlineData(Operation.Overwrite)]
    public async Task UpdatesWithoutETag_UseIfNoneMatch(Operation operation)
    {
        using var cancellation = new CancellationTokenSource();
        var storage = CreateStorage(operation, new BinarySerializer(), out _);
        var state = CreateState();
        state.ETag = null;
        state.RecordExists = false;
        var original = state.State;

        await RunAsync(storage, state, operation, cancellation.Token);

        Assert.Equal(cancellation.Token, Assert.Single(_blob.RequestTokens));
        Assert.Equal(ETag.All, _blob.Conditions!.IfNoneMatch);
        AssertSuccessfulState(state, original, operation);
    }

    private IGrainStorage CreateStorage(Operation operation, IGrainStorageSerializer serializer, out TestContainerFactory factory)
    {
        factory = new TestContainerFactory(new TestContainerClient(_blob));
        return new AzureBlobGrainStorage(
            "cancellation-tests",
            new AzureBlobStorageOptions
            {
                GrainStorageSerializer = serializer,
                DeleteStateOnClear = operation != Operation.Overwrite,
            },
            factory,
            _services.GetRequiredService<IActivatorProvider>(),
            _logger);
    }

    private static GrainState<TestState> CreateState() => new()
    {
        State = new TestState { Value = 123 },
        ETag = InitialETag,
        RecordExists = true,
    };

    private static Task RunAsync(IGrainStorage storage, GrainState<TestState> state, Operation operation, CancellationToken? token = null)
        => (operation, token) switch
        {
            (Operation.Read, { } cancellation) => storage.ReadStateAsync(GrainType, TestGrainId, state, cancellation),
            (Operation.Write, { } cancellation) => storage.WriteStateAsync(GrainType, TestGrainId, state, cancellation),
            (_, { } cancellation) => storage.ClearStateAsync(GrainType, TestGrainId, state, cancellation),
            (Operation.Read, null) => storage.ReadStateAsync(GrainType, TestGrainId, state),
            (Operation.Write, null) => storage.WriteStateAsync(GrainType, TestGrainId, state),
            _ => storage.ClearStateAsync(GrainType, TestGrainId, state),
        };

    private static void AssertUnchanged(GrainState<TestState> state, TestState? original)
    {
        Assert.NotNull(original);
        Assert.NotNull(state.State);
        Assert.Same(original, state.State);
        Assert.Equal(123, state.State.Value);
        Assert.Equal(InitialETag, state.ETag);
        Assert.True(state.RecordExists);
    }

    private static void AssertSuccessfulState(GrainState<TestState> state, TestState? original, Operation operation)
    {
        Assert.NotNull(original);
        Assert.NotNull(state.State);
        Assert.Equal(operation == Operation.Delete ? null : StoredETag, state.ETag);
        Assert.Equal(operation is Operation.Read or Operation.Write, state.RecordExists);
        Assert.Equal(operation switch { Operation.Read => 7, Operation.Write => 123, _ => 0 }, state.State.Value);
        if (operation == Operation.Write)
        {
            Assert.Same(original, state.State);
        }
        else
        {
            Assert.NotSame(original, state.State);
        }
    }

    private static async Task AwaitCancellationAsync(
        CancellationToken actualToken,
        CancellationToken expectedToken,
        TaskCompletionSource started,
        OperationCanceledException exception)
    {
        Assert.Equal(expectedToken, actualToken);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = actualToken.Register(() => completion.SetException(exception));
        started.SetResult();
        await completion.Task;
    }

    public enum Operation { Read, Write, Delete, Overwrite }

    public sealed class TestState
    {
        public int Value { get; set; }
    }

    private class BinarySerializer : IGrainStorageSerializer
    {
        public int CallCount { get; protected set; }

        public BinaryData Serialize<T>(T? input)
        {
            CallCount++;
            return BinaryData.FromObjectAsJson(input);
        }

        public T? Deserialize<T>(BinaryData input)
        {
            CallCount++;
            return input.ToObjectFromJson<T>();
        }
    }

    private sealed class StreamingSerializer : BinarySerializer, IGrainStorageStreamingSerializer
    {
        public List<CancellationToken> Tokens { get; } = [];

        public Func<CancellationToken, Task>? OnOperation { get; init; }

        public async ValueTask SerializeAsync<T>(T? input, Stream destination, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Tokens.Add(cancellationToken);
            if (OnOperation is { } operation)
            {
                await operation(cancellationToken);
            }

            await destination.WriteAsync(BinaryData.FromObjectAsJson(input).ToMemory(), cancellationToken);
        }

        public async ValueTask<T?> DeserializeAsync<T>(Stream input, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Tokens.Add(cancellationToken);
            if (OnOperation is { } operation)
            {
                await operation(cancellationToken);
            }

            return (await BinaryData.FromStreamAsync(input, cancellationToken)).ToObjectFromJson<T>();
        }
    }

    private sealed class TestContainerFactory(TestContainerClient container) : IBlobContainerFactory
    {
        public TestContainerClient Container => container;

        public int AccessCount { get; private set; }

        public BlobContainerClient GetBlobContainerClient(GrainId grainId)
        {
            Assert.Equal(TestGrainId, grainId);
            AccessCount++;
            return container;
        }

        public Task InitializeAsync(BlobServiceClient client) => throw new NotSupportedException();
    }

    private sealed class TestContainerClient(TestBlobClient blob) : BlobContainerClient
    {
        public override string Name => "test-container";

        public List<CancellationToken> CreateTokens { get; } = [];

        public Func<CancellationToken, Task>? OnCreate { get; set; }

        public override BlobClient GetBlobClient(string blobName)
        {
            Assert.Equal($"{GrainType}-{TestGrainId}.json", blobName);
            return blob;
        }

        public override async Task<Response<BlobContainerInfo>> CreateIfNotExistsAsync(
            PublicAccessType publicAccessType = PublicAccessType.None,
            IDictionary<string, string>? metadata = null,
            BlobContainerEncryptionScopeOptions? encryptionScopeOptions = null,
            CancellationToken cancellationToken = default)
        {
            CreateTokens.Add(cancellationToken);
            if (OnCreate is { } create)
            {
                await create(cancellationToken);
            }

            return Response.FromValue(BlobsModelFactory.BlobContainerInfo(new ETag(StoredETag), default), null!);
        }
    }

    private sealed class TestBlobClient : BlobClient
    {
        public override string Name => "test-blob";

        public override string BlobContainerName => "test-container";

        public List<CancellationToken> RequestTokens { get; } = [];

        public Func<CancellationToken, Task>? OnRequest { get; set; }

        public bool FailFirstUploadWithMissingContainer { get; set; }

        public Stream? Content { get; set; }

        public long? ContentLength { get; set; }

        public BlobRequestConditions? Conditions { get; private set; }

        public BinaryData? UploadedContent { get; private set; }

        public override async Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(
            BlobDownloadOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await ObserveRequestAsync(cancellationToken);
            var content = Content ?? new MemoryStream(BinaryData.FromObjectAsJson(new TestState { Value = 7 }).ToArray());
            var details = BlobsModelFactory.BlobDownloadDetails(
                contentLength: ContentLength ?? content.Length,
                eTag: new ETag(StoredETag));
            return Response.FromValue(BlobsModelFactory.BlobDownloadStreamingResult(content, details), null!);
        }

        public override async Task<Response<BlobContentInfo>> UploadAsync(
            BinaryData content,
            BlobUploadOptions options,
            CancellationToken cancellationToken = default)
        {
            Conditions = options.Conditions;
            await ObserveRequestAsync(cancellationToken);
            ThrowIfContainerMissing();
            UploadedContent = content;
            return UploadResponse();
        }

        public override async Task<Response<BlobContentInfo>> UploadAsync(
            Stream content,
            BlobUploadOptions options,
            CancellationToken cancellationToken = default)
        {
            Conditions = options.Conditions;
            await ObserveRequestAsync(cancellationToken);
            ThrowIfContainerMissing();
            UploadedContent = await BinaryData.FromStreamAsync(content, cancellationToken);
            return UploadResponse();
        }

        public override async Task<Response<bool>> DeleteIfExistsAsync(
            DeleteSnapshotsOption snapshotsOption = DeleteSnapshotsOption.None,
            BlobRequestConditions? conditions = null,
            CancellationToken cancellationToken = default)
        {
            Conditions = conditions;
            await ObserveRequestAsync(cancellationToken);
            return Response.FromValue(true, null!);
        }

        private async Task ObserveRequestAsync(CancellationToken cancellationToken)
        {
            RequestTokens.Add(cancellationToken);
            if (OnRequest is { } request)
            {
                await request(cancellationToken);
            }
        }

        private void ThrowIfContainerMissing()
        {
            if (FailFirstUploadWithMissingContainer)
            {
                FailFirstUploadWithMissingContainer = false;
                throw new RequestFailedException(404, "Container missing.", "ContainerNotFound", null);
            }
        }

        private static Response<BlobContentInfo> UploadResponse()
            => Response.FromValue(BlobsModelFactory.BlobContentInfo(new ETag(StoredETag), default, null, null, null, null, 0), null!);
    }

    private sealed class TokenTrackingStream() : MemoryStream(BinaryData.FromObjectAsJson(new TestState { Value = 7 }).ToArray())
    {
        public List<CancellationToken> Tokens { get; } = [];

        public Func<CancellationToken, Task>? OnRead { get; init; }

        public bool Disposed { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await ObserveReadAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }

        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await ObserveReadAsync(cancellationToken);
            await base.CopyToAsync(destination, bufferSize, cancellationToken);
        }

        private async Task ObserveReadAsync(CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            if (OnRead is { } read)
            {
                await read(cancellationToken);
            }
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class TestLogger : ILogger<AzureBlobGrainStorage>
    {
        public List<Exception> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                Assert.NotNull(exception);
                Errors.Add(exception);
            }
        }
    }
}
