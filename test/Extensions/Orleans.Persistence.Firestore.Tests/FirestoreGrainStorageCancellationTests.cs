using System.Reflection;
using Google.Api.Gax.Grpc;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;

namespace Orleans.Persistence.Firestore.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Persistence")]
[TestCategory("BVT")]
public sealed class FirestoreGrainStorageCancellationTests
{
    private static readonly GrainId GrainId = GrainId.Create("test", "cancellation");
    private const string StateName = "state";
    private const string ETag = "1.0";
    private const string NewETag = "2.0";

    public static TheoryData<string, string?, bool> Paths => new()
    {
        { "Read", ETag, false },
        { "Write", null, false },
        { "Write", " ", false },
        { "Write", ETag, false },
        { "Write", "*", false },
        { "Clear", null, false },
        { "Clear", ETag, false },
        { "Clear", "*", false },
        { "Clear", null, true },
        { "Clear", " ", true },
        { "Clear", ETag, true },
        { "Clear", "*", true },
    };

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task TokenOverloadsForwardCallerTokenAndPreserveSuccessfulStateTransitions(string operation, string? etag, bool deleteStateOnClear)
    {
        var fixture = new Fixture(deleteStateOnClear);
        fixture.Client.DocumentExists = operation == "Read";
        var state = CreateState(etag);
        var original = state.State;
        using var cancellation = new CancellationTokenSource();

        await Invoke(fixture.Storage, operation, state, cancellation.Token);

        AssertRequests(fixture.Client, operation, etag, deleteStateOnClear, cancellation.Token);
        AssertSuccessfulState(state, original, operation, deleteStateOnClear);
        Assert.Equal(operation == "Write" ? 1 : 0, fixture.Serializations);
        Assert.Equal(operation == "Read" ? 1 : 0, fixture.Deserializations);
        Assert.Equal(operation == "Clear" ? 1 : 0, fixture.Activations);
        Assert.Empty(fixture.Errors);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task LegacyOverloadsForwardNoneAndPreserveSuccessfulStateTransitions(string operation, string? etag, bool deleteStateOnClear)
    {
        var fixture = new Fixture(deleteStateOnClear);
        fixture.Client.DocumentExists = operation == "Read";
        var state = CreateState(etag);
        var original = state.State;

#pragma warning disable xUnit1051 // Exercise the legacy overloads which use CancellationToken.None.
        await Invoke(fixture.Storage, operation, state);
#pragma warning restore xUnit1051

        AssertRequests(fixture.Client, operation, etag, deleteStateOnClear, CancellationToken.None);
        AssertSuccessfulState(state, original, operation, deleteStateOnClear);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task PreCancellationPreservesStateAndSkipsRequestsSerializationAndActivation(string operation, string? etag, bool deleteStateOnClear)
    {
        var fixture = new Fixture(deleteStateOnClear);
        var state = CreateState(etag);
        var original = state.State;
        var token = new CancellationToken(canceled: true);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Invoke(fixture.Storage, operation, state, token));

        Assert.Equal(token, exception.CancellationToken);
        AssertUnchangedState(state, original, etag);
        Assert.Empty(fixture.Client.Requests);
        Assert.Equal(0, fixture.Serializations);
        Assert.Equal(0, fixture.Deserializations);
        Assert.Equal(0, fixture.Activations);
        Assert.Empty(fixture.Errors);
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Write")]
    [InlineData("Clear")]
    public async Task PreCancellationPrecedesInitializationAndStateAccess(string operation)
    {
        var fixture = new Fixture(initialized: false);
        var token = new CancellationToken(canceled: true);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Invoke(fixture.Storage, operation, null!, token));

        Assert.Equal(token, exception.CancellationToken);
        Assert.Empty(fixture.Client.Requests);
        Assert.Empty(fixture.Errors);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task InFlightCancellationPreservesExceptionStateAndSkipsErrorLogging(string operation, string? etag, bool deleteStateOnClear)
    {
        var fixture = new Fixture(deleteStateOnClear);
        var state = CreateState(etag);
        var original = state.State;
        using var cancellation = new CancellationTokenSource();
        var failure = new OperationCanceledException("native cancellation", cancellation.Token);
        fixture.Client.Block = true;
        fixture.Client.CancellationFailure = failure;

        var pending = Invoke(fixture.Storage, operation, state, cancellation.Token);
        await fixture.Client.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        Assert.Equal(cancellation.Token, Assert.Single(fixture.Client.Requests).Token);
        cancellation.Cancel();
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => pending);

        Assert.Same(failure, exception);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        AssertUnchangedState(state, original, etag);
        Assert.Equal(0, fixture.Deserializations);
        Assert.Equal(0, fixture.Activations);
        Assert.Empty(fixture.Errors);
    }

    [Fact]
    public async Task ExistenceCheckRejectsExistingDocumentAndPreservesState()
    {
        var fixture = new Fixture(deleteStateOnClear: true);
        fixture.Client.DocumentExists = true;
        var state = CreateState(null);
        var original = state.State;

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => Invoke(fixture.Storage, "Clear", state, TestContext.Current.CancellationToken));

        Assert.Null(exception.CurrentEtag);
        Assert.Equal("Unknown", exception.StoredEtag);
        AssertRequests(fixture.Client, "Clear", null, true, TestContext.Current.CancellationToken);
        AssertUnchangedState(state, original, null);
        Assert.Equal(0, fixture.Activations);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task CallerCancellationTranslatesNativeCancelledRpcAndPreservesState(string operation, string? etag, bool deleteStateOnClear)
    {
        var fixture = new Fixture(deleteStateOnClear);
        var state = CreateState(etag);
        var original = state.State;
        using var cancellation = new CancellationTokenSource();
        var failure = new RpcException(new Status(StatusCode.Cancelled, "native cancellation"));
        fixture.Client.BeforeResponse = cancellation.Cancel;
        fixture.Client.Failure = failure;

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => Invoke(fixture.Storage, operation, state, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Same(failure, exception.InnerException);
        AssertUnchangedState(state, original, etag);
        Assert.Empty(fixture.Errors);
    }

    [Theory]
    [InlineData("Write", false)]
    [InlineData("Clear", false)]
    [InlineData("Clear", true)]
    public async Task ConcurrencyFailuresPreserveETagAndState(string operation, bool deleteStateOnClear)
    {
        var fixture = new Fixture(deleteStateOnClear);
        var failure = new RpcException(new Status(StatusCode.FailedPrecondition, "etag conflict"));
        fixture.Client.Failure = failure;
        var state = CreateState(ETag);
        var original = state.State;

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => Invoke(fixture.Storage, operation, state, TestContext.Current.CancellationToken));

        Assert.Equal(ETag, exception.CurrentEtag);
        Assert.Equal("Unknown", exception.StoredEtag);
        if (!deleteStateOnClear)
        {
            Assert.Same(failure, exception.InnerException);
        }

        AssertUnchangedState(state, original, ETag);
    }

    [Theory]
    [InlineData("Write", StatusCode.PermissionDenied)]
    [InlineData("Clear", StatusCode.PermissionDenied)]
    [InlineData("Write", StatusCode.Cancelled)]
    [InlineData("Clear", StatusCode.Cancelled)]
    public async Task OrdinaryFailuresPropagateAndAreLogged(string operation, StatusCode status)
    {
        var fixture = new Fixture();
        var failure = new RpcException(new Status(status, "native failure"));
        fixture.Client.Failure = failure;
        var state = CreateState(ETag);
        var original = state.State;

        var exception = await Assert.ThrowsAsync<RpcException>(
            () => Invoke(fixture.Storage, operation, state, TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        AssertUnchangedState(state, original, ETag);
        Assert.Contains(failure, fixture.Errors);
    }

    [Theory]
    [InlineData("Write", false, false)]
    [InlineData("Write", false, true)]
    [InlineData("Clear", false, false)]
    [InlineData("Clear", false, true)]
    [InlineData("Clear", true, false)]
    [InlineData("Clear", true, true)]
    public async Task UnrequestedSdkCancellationPreservesExceptionStateAndErrorLogging(string operation, bool deleteStateOnClear, bool legacy)
    {
        var fixture = new Fixture(deleteStateOnClear);
        var failure = new OperationCanceledException("SDK cancellation", new CancellationToken(canceled: true));
        fixture.Client.Failure = failure;
        var state = CreateState(ETag);
        var original = state.State;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

#pragma warning disable xUnit1051 // Verify diagnostics for both legacy None and cancellation-aware calls.
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => legacy
                ? Invoke(fixture.Storage, operation, state)
                : Invoke(fixture.Storage, operation, state, cancellation.Token));
#pragma warning restore xUnit1051

        Assert.False(cancellation.IsCancellationRequested);
        Assert.Same(failure, exception);
        Assert.Equal(failure.CancellationToken, exception.CancellationToken);
        AssertUnchangedState(state, original, ETag);
        Assert.Equal(legacy ? CancellationToken.None : cancellation.Token, Assert.Single(fixture.Client.Requests).Token);
        Assert.Same(failure, Assert.Single(fixture.Errors));
        Assert.Equal(0, fixture.Activations);
    }

    private static GrainState<State> CreateState(string? etag) => new(new State { Value = 7 }, etag) { RecordExists = true };

    private static Task Invoke(IGrainStorage storage, string operation, GrainState<State> state, CancellationToken token) => operation switch
    {
        "Read" => storage.ReadStateAsync(StateName, GrainId, state, token),
        "Write" => storage.WriteStateAsync(StateName, GrainId, state, token),
        "Clear" => storage.ClearStateAsync(StateName, GrainId, state, token),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static Task Invoke(IGrainStorage storage, string operation, GrainState<State> state) => operation switch
    {
        "Read" => storage.ReadStateAsync(StateName, GrainId, state),
        "Write" => storage.WriteStateAsync(StateName, GrainId, state),
        "Clear" => storage.ClearStateAsync(StateName, GrainId, state),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static void AssertUnchangedState(GrainState<State> state, State? original, string? etag)
    {
        Assert.Same(original, state.State);
        Assert.Equal(7, state.State!.Value);
        Assert.Equal(etag, state.ETag);
        Assert.True(state.RecordExists);
    }

    private static void AssertSuccessfulState(GrainState<State> state, State? original, string operation, bool deleteStateOnClear)
    {
        Assert.Equal(operation != "Clear", state.RecordExists);
        Assert.Equal(operation == "Clear" && deleteStateOnClear ? null : NewETag, state.ETag);
        Assert.Equal(operation == "Clear" ? 0 : operation == "Read" ? 42 : 7, state.State!.Value);
        if (operation == "Write")
        {
            Assert.Same(original, state.State);
        }
        else
        {
            Assert.NotSame(original, state.State);
        }
    }

    private static void AssertRequests(StorageClient client, string operation, string? etag, bool deleteStateOnClear, CancellationToken token)
    {
        var expected = operation == "Read" || operation == "Clear" && deleteStateOnClear && string.IsNullOrWhiteSpace(etag)
            ? new[] { "Read" }
            : new[] { "Commit" };
        Assert.Equal(expected, client.Requests.Select(request => request.Method));
        Assert.All(client.Requests, request => Assert.Equal(token, request.Token));
        if (operation == "Read" || operation == "Clear" && deleteStateOnClear && string.IsNullOrWhiteSpace(etag))
        {
            Assert.Empty(client.Writes);
            return;
        }

        var write = Assert.Single(client.Writes);
        Assert.Equal(operation == "Clear" && deleteStateOnClear, write.OperationCase == Write.OperationOneofCase.Delete);
        if (write.OperationCase == Write.OperationOneofCase.Update)
        {
            Assert.Equal(StateName, write.Update.Fields["Name"].StringValue);
            if (operation == "Write")
            {
                Assert.Equal(ByteString.CopyFrom(new byte[] { 1 }), write.Update.Fields["Payload"].BytesValue);
            }
            else
            {
                if (etag is null)
                {
                    Assert.Equal(Value.ValueTypeOneofCase.NullValue, write.Update.Fields["Payload"].ValueTypeCase);
                }
                else
                {
                    Assert.False(write.Update.Fields.ContainsKey("Payload"));
                    Assert.Contains("Payload", write.UpdateMask.FieldPaths);
                }
            }
        }

        if (etag is "*" || string.IsNullOrWhiteSpace(etag))
        {
            Assert.Equal(Google.Cloud.Firestore.V1.Precondition.ConditionTypeOneofCase.Exists, write.CurrentDocument.ConditionTypeCase);
            Assert.Equal(etag == "*", write.CurrentDocument.Exists);
        }
        else
        {
            Assert.Equal(Timestamp.FromDateTime(DateTime.UnixEpoch.AddSeconds(1)), write.CurrentDocument.UpdateTime);
        }
    }

    private sealed class State
    {
        public int Value { get; init; }
    }

    private sealed class Fixture : IGrainStorageSerializer, IActivatorProvider, ILoggerFactory, ILogger
    {
        public StorageClient Client { get; } = new();
        public IGrainStorage Storage { get; }
        public int Serializations { get; private set; }
        public int Deserializations { get; private set; }
        public int Activations { get; private set; }
        public List<Exception?> Errors { get; } = [];

        public Fixture(bool deleteStateOnClear = false, bool initialized = true)
        {
            var options = new FirestoreStateStorageOptions
            {
                ProjectId = "test-project",
                EmulatorHost = "127.0.0.1:1",
                DeleteStateOnClear = deleteStateOnClear,
                GrainStorageSerializer = this,
            };
            var storage = new FirestoreGrainStorage("test", options, Options.Create(new ClusterOptions()), this, this);
            if (initialized)
            {
                var manager = new FirestoreDataManager("Persistence", "test", options, this);
                // Use the native SDK with the same transport seam as the Firestore membership tests.
                typeof(FirestoreDataManager).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(manager, FirestoreDb.Create(options.ProjectId, Client));
                typeof(FirestoreGrainStorage).GetField("_dataManager", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(storage, manager);
            }

            Storage = storage;
        }

        public BinaryData Serialize<T>(T? input)
        {
            Serializations++;
            return new BinaryData(new byte[] { 1 });
        }

        public T? Deserialize<T>(BinaryData input)
        {
            Deserializations++;
            return new State { Value = 42 } is T result ? result : throw new InvalidOperationException();
        }

        public IActivator<T> GetActivator<T>() => new StateActivator<T>(this);
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                Errors.Add(exception);
            }
        }

        private sealed class StateActivator<T>(Fixture fixture) : IActivator<T>
        {
            public T Create()
            {
                fixture.Activations++;
                return new State() is T result ? result : throw new InvalidOperationException();
            }
        }
    }

    private sealed class StorageClient : FirestoreClient
    {
        public override FirestoreSettings Settings => FirestoreSettings.GetDefault();
        public List<(string Method, CancellationToken Token)> Requests { get; } = [];
        public List<Write> Writes { get; } = [];
        public bool DocumentExists { get; set; }
        public bool Block { get; set; }
        public Action? BeforeResponse { get; set; }
        public Exception? Failure { get; set; }
        public OperationCanceledException? CancellationFailure { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override BatchGetDocumentsStream BatchGetDocuments(BatchGetDocumentsRequest request, CallSettings? callSettings = null)
        {
            var token = Record("Read", callSettings);
            var response = new BatchGetDocumentsResponse { ReadTime = Timestamp.FromDateTime(DateTime.UnixEpoch.AddSeconds(2)) };
            if (DocumentExists)
            {
                response.Found = new Document
                {
                    Name = Assert.Single(request.Documents),
                    CreateTime = Timestamp.FromDateTime(DateTime.UnixEpoch),
                    UpdateTime = response.ReadTime,
                    Fields = { ["Payload"] = new Value { BytesValue = ByteString.CopyFrom(new byte[] { 1 }) } },
                };
            }
            else
            {
                response.Missing = Assert.Single(request.Documents);
            }

            return new DocumentStream(new ResponseReader(response, () => Respond(true, token)));
        }

        public override Task<CommitResponse> CommitAsync(CommitRequest request, CallSettings? callSettings = null)
        {
            Writes.AddRange(request.Writes);
            var response = new CommitResponse { CommitTime = Timestamp.FromDateTime(DateTime.UnixEpoch.AddSeconds(2)) };
            foreach (var write in request.Writes)
            {
                response.WriteResults.Add(new Google.Cloud.Firestore.V1.WriteResult { UpdateTime = response.CommitTime });
            }

            return Respond(response, Record("Commit", callSettings));
        }

        private CancellationToken Record(string method, CallSettings? settings)
        {
            var token = settings?.CancellationToken ?? CancellationToken.None;
            Requests.Add((method, token));
            return token;
        }

        private async Task<T> Respond<T>(T response, CancellationToken token)
        {
            BeforeResponse?.Invoke();
            if (Failure is { } failure)
            {
                throw failure;
            }

            if (!Block)
            {
                return response;
            }

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => completion.TrySetException(CancellationFailure!));
            Started.TrySetResult();
            return await completion.Task;
        }
    }

    private sealed class DocumentStream(ResponseReader reader) : FirestoreClient.BatchGetDocumentsStream
    {
        public override AsyncServerStreamingCall<BatchGetDocumentsResponse> GrpcCall { get; } = new(
            reader, Task.FromResult(new global::Grpc.Core.Metadata()), () => Status.DefaultSuccess, () => new global::Grpc.Core.Metadata(), () => { });
    }

    private sealed class ResponseReader(BatchGetDocumentsResponse response, Func<Task<bool>> read) : IAsyncStreamReader<BatchGetDocumentsResponse>
    {
        private bool _read;
        public BatchGetDocumentsResponse Current => response;
        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (_read)
            {
                return Task.FromResult(false);
            }

            _read = true;
            return read();
        }
    }
}
