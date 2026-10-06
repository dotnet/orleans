using System.Net;
using System.Reflection;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.Extensions;
using Orleans.Configuration;
using Orleans.Persistence.Cosmos;
using Orleans.Runtime;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Serializers;

namespace Tester.Cosmos.Persistence;

[TestCategory("Cosmos"), TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("Cosmos")]
[TestArea("Persistence")]
public sealed class CosmosGrainStorageCancellationTests
{
    public static TheoryData<string, string?, bool> Operations => new()
    {
        { "Read", "original-etag", false },
        { "Write", null, false },
        { "Write", "", false },
        { "Write", " ", false },
        { "Write", "*", false },
        { "Write", "original-etag", false },
        { "Clear", null, true },
        { "Clear", "", true },
        { "Clear", "original-etag", true },
        { "Clear", "*", true },
        { "Clear", null, false },
        { "Clear", "", false },
        { "Clear", "*", false },
        { "Clear", "original-etag", false },
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task StorageOperationsForwardCallerToken(string operation, string? etag, bool deleteOnClear)
    {
        using var harness = new StorageHarness(deleteOnClear);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var state = CreateState(etag);
        var original = Assert.IsType<TestState>(state.State);
        harness.SetSuccess(operation, etag, deleteOnClear);

        await Invoke(harness.Storage, operation, state, cancellation.Token);

        Assert.Equal(1, harness.Identifiers.CallCount);
        Assert.Equal(cancellation.Token, harness.Executor.Token);
        Assert.Equal(1, harness.Executor.CallCount);
        AssertRequest(harness, operation, etag, deleteOnClear, cancellation.Token, original);
        AssertSuccessfulState(harness, operation, deleteOnClear, state, original);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task LegacyStorageOperationsUseNone(string operation, string? etag, bool deleteOnClear)
    {
        using var harness = new StorageHarness(deleteOnClear);
        var state = CreateState(etag);
        var original = Assert.IsType<TestState>(state.State);
        harness.SetSuccess(operation, etag, deleteOnClear);

        await InvokeLegacy(harness.Storage, operation, state);

        Assert.Equal(CancellationToken.None, harness.Executor.Token);
        AssertRequest(harness, operation, etag, deleteOnClear, CancellationToken.None, original);
        AssertSuccessfulState(harness, operation, deleteOnClear, state, original);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task PreCanceledOperationsPreserveState(string operation, string? etag, bool deleteOnClear)
    {
        using var harness = new StorageHarness(deleteOnClear);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var state = CreateState(etag);
        var original = Assert.IsType<TestState>(state.State);

        var task = Invoke(harness.Storage, operation, state, cancellation.Token);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(task.IsCanceled);
        Assert.Equal(0, harness.Identifiers.CallCount);
        Assert.Equal(0, harness.Executor.CallCount);
        Assert.Empty(harness.Container.ReceivedCalls());
        AssertPreservedState(state, original, etag);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task InFlightCancellationPropagatesUnchangedAndPreservesState(string operation, string? etag, bool deleteOnClear)
    {
        using var harness = new StorageHarness(deleteOnClear);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<ItemResponse<GrainStateEntity<TestState>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new OperationCanceledException("native Cosmos cancellation", cancellation.Token);
        using var registration = cancellation.Token.Register(() => completion.SetException(failure));
        harness.Container.ReturnsForAll<Task<ItemResponse<GrainStateEntity<TestState>>>>(call =>
        {
            started.SetResult(call.Arg<CancellationToken>());
            return completion.Task;
        });
        var state = CreateState(etag);
        var original = Assert.IsType<TestState>(state.State);

        var task = Invoke(harness.Storage, operation, state, cancellation.Token);
        Assert.Equal(cancellation.Token, await started.Task.WaitAsync(TestContext.Current.CancellationToken));
        Assert.False(task.IsCompleted);
        cancellation.Cancel();
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => task);

        Assert.Same(failure, exception);
        Assert.True(task.IsCanceled);
        AssertRequest(harness, operation, etag, deleteOnClear, cancellation.Token, original);
        AssertPreservedState(state, original, etag);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task CancellationDuringDocumentIdentifiersPreservesState(string operation, string? etag, bool deleteOnClear)
    {
        using var harness = new StorageHarness(deleteOnClear);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var identifiers = new TaskCompletionSource<(string DocumentId, string PartitionKey)>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Identifiers.Completion = identifiers.Task;
        var state = CreateState(etag);
        var original = Assert.IsType<TestState>(state.State);

        var task = Invoke(harness.Storage, operation, state, cancellation.Token);
        Assert.Equal(1, harness.Identifiers.CallCount);
        Assert.False(task.IsCompleted);
        cancellation.Cancel();
        identifiers.SetResult(("document", "partition"));
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(task.IsCanceled);
        Assert.Equal(0, harness.Executor.CallCount);
        Assert.Empty(harness.Container.ReceivedCalls());
        AssertPreservedState(state, original, etag);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task DefaultExecutorCancelsThrottlingWaitAndPreservesState(string operation, string? etag, bool deleteOnClear)
    {
        using var harness = new StorageHarness(deleteOnClear, DefaultCosmosOperationExecutor.Instance);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Container.ReturnsForAll<Task<ItemResponse<GrainStateEntity<TestState>>>>(call =>
        {
            Assert.Equal(cancellation.Token, call.Arg<CancellationToken>());
            requested.SetResult();
            return Task.FromException<ItemResponse<GrainStateEntity<TestState>>>(
                new ThrottledException(TimeSpan.FromDays(1)));
        });
        var state = CreateState(etag);
        var original = Assert.IsType<TestState>(state.State);

        var task = Invoke(harness.Storage, operation, state, cancellation.Token);
        await requested.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted);
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(task.IsCanceled);
        AssertRequest(harness, operation, etag, deleteOnClear, cancellation.Token, original);
        AssertPreservedState(state, original, etag);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultExecutorRetriesThrottlingWithCallerToken(bool aggregate)
    {
        using var harness = new StorageHarness(executor: DefaultCosmosOperationExecutor.Instance);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var attempts = 0;
        harness.Container.ReturnsForAll<Task<ItemResponse<GrainStateEntity<TestState>>>>(call =>
        {
            Assert.Equal(cancellation.Token, call.Arg<CancellationToken>());
            attempts++;
            if (attempts == 1)
            {
                var throttled = new ThrottledException(TimeSpan.Zero);
                return Task.FromException<ItemResponse<GrainStateEntity<TestState>>>(
                    aggregate ? new AggregateException(throttled) : throttled);
            }

            return Task.FromResult(harness.Response);
        });
        var state = CreateState("original-etag");

        await Invoke(harness.Storage, "Write", state, cancellation.Token);

        Assert.Equal(2, attempts);
        Assert.Equal(2, harness.Container.ReceivedCalls().Count());
        Assert.Equal("updated-etag", state.ETag);
        Assert.True(state.RecordExists);
    }

    [Fact]
    public async Task LegacyExecutorRemainsCompatible()
    {
        var executor = new LegacyExecutor();
        using var harness = new StorageHarness(executor: executor);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var state = CreateState("original-etag");
        var original = Assert.IsType<TestState>(state.State);
        harness.SetSuccess("Write", state.ETag, false);

        await Invoke(harness.Storage, "Write", state, cancellation.Token);

        Assert.Equal(1, executor.CallCount);
        AssertRequest(harness, "Write", "original-etag", false, cancellation.Token, original);
        Assert.Equal("updated-etag", state.ETag);
        Assert.True(state.RecordExists);
    }

    [Fact]
    public async Task ClearPrerequisiteReadPreservesInconsistentStateBehavior()
    {
        using var harness = new StorageHarness(deleteOnClear: true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        harness.Container.ReturnsForAll(Task.FromResult(harness.Response));
        var state = CreateState(null);
        var original = Assert.IsType<TestState>(state.State);

        var exception = await Assert.ThrowsAsync<WrappedException>(
            () => Invoke(harness.Storage, "Clear", state, cancellation.Token));

        Assert.Contains(nameof(CosmosConditionNotSatisfiedException), exception.OriginalExceptionType);
        Assert.Contains("StoredETag: None, CurrentETag: updated-etag", exception.Message);
        AssertRequest(harness, "Clear", null, true, cancellation.Token, original);
        AssertPreservedState(state, original, null);
    }

    [Theory]
    [InlineData("Write", HttpStatusCode.PreconditionFailed)]
    [InlineData("Write", HttpStatusCode.Conflict)]
    [InlineData("Write", HttpStatusCode.NotFound)]
    [InlineData("Clear", HttpStatusCode.PreconditionFailed)]
    [InlineData("Clear", HttpStatusCode.Conflict)]
    [InlineData("Clear", HttpStatusCode.NotFound)]
    public async Task WriteAndClearRetainConcurrencyFailures(string operation, HttpStatusCode status)
    {
        using var harness = new StorageHarness();
        harness.Container.ReturnsForAll(Task.FromException<ItemResponse<GrainStateEntity<TestState>>>(
            new CosmosException("concurrency failure", status, 0, "", 0)));
        var state = CreateState("original-etag");
        var original = Assert.IsType<TestState>(state.State);

        var exception = await Assert.ThrowsAsync<CosmosConditionNotSatisfiedException>(
            () => Invoke(harness.Storage, operation, state, TestContext.Current.CancellationToken));

        Assert.Equal("Unknown", exception.StoredEtag);
        Assert.Equal("original-etag", exception.CurrentEtag);
        AssertPreservedState(state, original, "original-etag");
    }

    [Theory]
    [InlineData("Read", false, false)]
    [InlineData("Write", false, false)]
    [InlineData("Clear", false, false)]
    [InlineData("Read", true, false)]
    [InlineData("Write", true, false)]
    [InlineData("Clear", true, false)]
    [InlineData("Read", true, true)]
    [InlineData("Write", true, true)]
    [InlineData("Clear", true, true)]
    public async Task StorageFailuresRetainWrapping(string operation, bool sdkCancellation, bool legacy)
    {
        using var harness = new StorageHarness();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Exception failure = sdkCancellation
            ? new OperationCanceledException("storage failure", new CancellationToken(canceled: true))
            : new InvalidOperationException("storage failure");
        harness.Container.ReturnsForAll(Task.FromException<ItemResponse<GrainStateEntity<TestState>>>(failure));
        var state = CreateState("original-etag");
        var original = Assert.IsType<TestState>(state.State);

        Assert.False(cancellation.IsCancellationRequested);
        var task = legacy
            ? InvokeLegacy(harness.Storage, operation, state)
            : Invoke(harness.Storage, operation, state, cancellation.Token);
        var exception = await Assert.ThrowsAsync<WrappedException>(() => task);

        Assert.False(cancellation.IsCancellationRequested);
        Assert.True(task.IsFaulted);
        Assert.Contains("storage failure", exception.Message);
        Assert.Contains(failure.GetType().Name, exception.OriginalExceptionType);
        AssertPreservedState(state, original, "original-etag");
    }

    private static GrainState<TestState> CreateState(string? etag) => new()
    {
        State = new TestState { Value = "original" },
        ETag = etag,
        RecordExists = true,
    };

    private static Task Invoke(CosmosGrainStorage storage, string operation, GrainState<TestState> state, CancellationToken token)
    {
        Orleans.Storage.IGrainStorage provider = storage;
        var grainId = GrainId.Create("grain/type", "grain/key");
        return operation switch
        {
            "Read" => provider.ReadStateAsync("grain-type", grainId, state, token),
            "Write" => provider.WriteStateAsync("grain-type", grainId, state, token),
            "Clear" => provider.ClearStateAsync("grain-type", grainId, state, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    private static Task InvokeLegacy(CosmosGrainStorage storage, string operation, GrainState<TestState> state)
    {
        var grainId = GrainId.Create("grain/type", "grain/key");
        return operation switch
        {
            "Read" => storage.ReadStateAsync("grain-type", grainId, state),
            "Write" => storage.WriteStateAsync("grain-type", grainId, state),
            "Clear" => storage.ClearStateAsync("grain-type", grainId, state),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    private static void AssertRequest(StorageHarness harness, string operation, string? etag, bool deleteOnClear, CancellationToken token, TestState original)
    {
        var expectedMethod = operation switch
        {
            "Read" => "ReadItemAsync",
            "Write" when string.IsNullOrWhiteSpace(etag) => "CreateItemAsync",
            "Write" when etag == "*" => "UpsertItemAsync",
            "Write" => "ReplaceItemAsync",
            "Clear" when deleteOnClear && string.IsNullOrWhiteSpace(etag) => "ReadItemAsync",
            "Clear" when deleteOnClear => "DeleteItemAsync",
            "Clear" when string.IsNullOrEmpty(etag) => "CreateItemAsync",
            "Clear" => "ReplaceItemAsync",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        var call = Assert.Single(harness.Container.ReceivedCalls());
        Assert.Equal(expectedMethod, call.GetMethodInfo().Name);
        var arguments = call.GetArguments();
        Assert.Equal(token, Assert.IsType<CancellationToken>(arguments[^1]));
        var partitionIndex = expectedMethod == "ReplaceItemAsync" ? 2 : 1;
        Assert.Equal(new PartitionKey("partition"), Assert.IsType<PartitionKey>(arguments[partitionIndex]));
        if (expectedMethod is "ReadItemAsync" or "DeleteItemAsync")
        {
            Assert.Equal("document", arguments[0]);
        }
        else
        {
            var entity = Assert.IsType<GrainStateEntity<TestState>>(arguments[0]);
            Assert.Equal("document", entity.Id);
            Assert.Equal("partition", entity.PartitionKey);
            Assert.Equal("grain-type", entity.GrainType);
            Assert.Equal(etag, entity.ETag);
            if (operation == "Write")
            {
                Assert.Same(original, entity.State);
            }
            else
            {
                Assert.Null(entity.State);
            }

            if (expectedMethod == "ReplaceItemAsync")
            {
                Assert.Equal("document", arguments[1]);
            }
        }

        if (expectedMethod is "CreateItemAsync" or "ReadItemAsync")
        {
            Assert.Null(arguments[^2]);
        }
        else
        {
            Assert.Equal(etag, Assert.IsType<ItemRequestOptions>(arguments[^2]).IfMatchEtag);
        }
    }

    private static void AssertPreservedState(GrainState<TestState> state, TestState original, string? etag)
    {
        Assert.Same(original, state.State);
        Assert.Equal("original", original.Value);
        Assert.Equal(etag, state.ETag);
        Assert.True(state.RecordExists);
    }

    private static void AssertSuccessfulState(StorageHarness harness, string operation, bool deleteOnClear, GrainState<TestState> state, TestState original)
    {
        Assert.Equal(operation == "Clear" && deleteOnClear ? null : "updated-etag", state.ETag);
        Assert.Equal(operation != "Clear", state.RecordExists);
        var current = Assert.IsType<TestState>(state.State);
        switch (operation)
        {
            case "Read":
                Assert.Same(harness.Response.Resource.State, state.State);
                Assert.Equal("stored", current.Value);
                break;
            case "Write":
                Assert.Same(original, state.State);
                break;
            case "Clear":
                Assert.NotSame(original, state.State);
                Assert.Null(current.Value);
                break;
        }
    }

    private sealed class StorageHarness : IDisposable
    {
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();

        public StorageHarness(bool deleteOnClear = false, ICosmosOperationExecutor? executor = null)
        {
            var entity = new GrainStateEntity<TestState>
            {
                ETag = "updated-etag",
                State = new TestState { Value = "stored" },
            };
            Response = new StateResponse(entity);
            Container.ReturnsForAll<Task<ItemResponse<GrainStateEntity<TestState>>>>(
                Task.FromException<ItemResponse<GrainStateEntity<TestState>>>(new InvalidOperationException("Unexpected storage operation.")));
            Storage = new CosmosGrainStorage(
                "storage",
                new CosmosGrainStorageOptions
                {
                    DeleteStateOnClear = deleteOnClear,
                    OperationExecutor = executor ?? Executor,
                },
                NullLoggerFactory.Instance, _services,
                Options.Create(new ClusterOptions { ServiceId = "service" }),
                Identifiers, new ActivatorProvider());
            typeof(CosmosGrainStorage).GetField("_container", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Storage, Container);
        }

        public CosmosGrainStorage Storage { get; }
        public Container Container { get; } = Substitute.For<Container>();
        public ItemResponse<GrainStateEntity<TestState>> Response { get; }
        public DocumentIdProvider Identifiers { get; } = new();
        public RecordingExecutor Executor { get; } = new();

        public void SetSuccess(string operation, string? etag, bool deleteOnClear)
        {
            Container.ReturnsForAll(operation == "Clear" && deleteOnClear && string.IsNullOrWhiteSpace(etag)
                ? Task.FromException<ItemResponse<GrainStateEntity<TestState>>>(new CosmosException("missing", HttpStatusCode.NotFound, 0, "", 0))
                : Task.FromResult(Response));
        }

        public void Dispose() => _services.Dispose();
    }

    private sealed class StateResponse(GrainStateEntity<TestState> entity) : ItemResponse<GrainStateEntity<TestState>>
    {
        public override GrainStateEntity<TestState> Resource => entity;
        public override string? ETag => entity.ETag;
    }

    private sealed class DocumentIdProvider : IDocumentIdProvider
    {
        public int CallCount { get; private set; }
        public Task<(string DocumentId, string PartitionKey)>? Completion { get; set; }

        public ValueTask<(string DocumentId, string PartitionKey)> GetDocumentIdentifiers(string grainType, GrainId grainId)
        {
            CallCount++;
            return Completion is { } task ? new(task) : new(("document", "partition"));
        }
    }

    private sealed class RecordingExecutor : ICosmosOperationExecutor
    {
        public CancellationToken Token { get; private set; }
        public int CallCount { get; private set; }

        public Task<TResult> ExecuteOperation<TArg, TResult>(Func<TArg, Task<TResult>> func, TArg arg)
            => throw new InvalidOperationException("Expected the cancellation-aware executor overload.");

        public Task<TResult> ExecuteOperation<TArg, TResult>(Func<TArg, Task<TResult>> func, TArg arg, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return func(arg);
        }
    }

    private sealed class LegacyExecutor : ICosmosOperationExecutor
    {
        public int CallCount { get; private set; }

        public Task<TResult> ExecuteOperation<TArg, TResult>(Func<TArg, Task<TResult>> func, TArg arg)
        {
            CallCount++;
            return func(arg);
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

    private sealed class ThrottledException(TimeSpan retryAfter) : CosmosException("throttled", HttpStatusCode.TooManyRequests, 0, "", 0)
    {
        public override TimeSpan? RetryAfter => retryAfter;
    }

    public sealed class TestState
    {
        public string? Value { get; set; }
    }
}
