using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.Transactions.Abstractions;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestCategory("BVT"), TestCategory("Transactions")]
public sealed class TransactionalStateStorageProviderWrapperTests
{
    [Fact]
    public async Task FailedWriteDoesNotMutateCachedStateAndAllowsSameInstanceReuse()
    {
        using var fixture = new Fixture(CreateInitialState());
        var loaded = await fixture.Storage.Load();
        var speculativeMetadata = CreateMetadata(20);
        fixture.Provider.FailNextWrite = true;

        await Assert.ThrowsAsync<OrleansException>(() => fixture.Storage.Store(
            loaded.ETag,
            speculativeMetadata,
            [CreatePendingState(3, "speculative")],
            commitUpTo: 2,
            abortAfter: null));

        Assert.Equal("speculative-etag", fixture.Provider.LastAttemptedETag);
        Assert.Equal(2, fixture.Provider.LastAttemptedState!.CommittedSequenceId);
        Assert.Equal("prepared", fixture.Provider.LastAttemptedState.CommittedState.Value);
        Assert.Equal(20, fixture.Provider.LastAttemptedState.Metadata.TimeStamp.Ticks);
        Assert.Collection(
            fixture.Provider.LastAttemptedState.PendingStates,
            state => AssertPendingState(state, 3, "speculative"));

        Assert.Equal("etag-1", loaded.ETag);
        Assert.Equal(1, loaded.CommittedSequenceId);
        Assert.Equal("committed", loaded.CommittedState.Value);
        Assert.Equal(10, loaded.Metadata.TimeStamp.Ticks);
        Assert.Collection(loaded.PendingStates, state => AssertPendingState(state, 2, "prepared"));

        var recoveryMetadata = CreateMetadata(30);
        var etag = await fixture.Storage.Store(
            loaded.ETag,
            recoveryMetadata,
            statesToPrepare: null,
            commitUpTo: null,
            abortAfter: null);

        Assert.Equal("etag-2", etag);
        Assert.Equal(1, fixture.Provider.DurableState.CommittedSequenceId);
        Assert.Equal("committed", fixture.Provider.DurableState.CommittedState.Value);
        Assert.Equal(30, fixture.Provider.DurableState.Metadata.TimeStamp.Ticks);
        Assert.Collection(
            fixture.Provider.DurableState.PendingStates,
            state => AssertPendingState(state, 2, "prepared"));
    }

    [Fact]
    public async Task LoadAfterAmbiguousFailureRecoversPersistedState()
    {
        using var fixture = new Fixture(CreateInitialState());
        var loaded = await fixture.Storage.Load();
        fixture.Provider.FailNextWrite = true;
        fixture.Provider.PersistFailedWrite = true;

        await Assert.ThrowsAsync<OrleansException>(() => fixture.Storage.Store(
            loaded.ETag,
            CreateMetadata(20),
            statesToPrepare: null,
            commitUpTo: 2,
            abortAfter: null));

        var recovered = await fixture.Storage.Load();

        Assert.Equal("etag-ambiguous", recovered.ETag);
        Assert.Equal(2, recovered.CommittedSequenceId);
        Assert.Equal("prepared", recovered.CommittedState.Value);
        Assert.Equal(20, recovered.Metadata.TimeStamp.Ticks);
        Assert.Empty(recovered.PendingStates);

        var etag = await fixture.Storage.Store(
            recovered.ETag,
            recovered.Metadata,
            statesToPrepare: null,
            commitUpTo: null,
            abortAfter: null);

        Assert.Equal("etag-2", etag);
        Assert.Equal(2, fixture.Provider.DurableState.CommittedSequenceId);
        Assert.Equal("prepared", fixture.Provider.DurableState.CommittedState.Value);
    }

    [Fact]
    public async Task StoreRequiresMatchingETagAndPreservesNoChangeWrites()
    {
        using var fixture = new Fixture(CreateInitialState());
        var loaded = await fixture.Storage.Load();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Storage.Store(
            "stale-etag",
            loaded.Metadata,
            statesToPrepare: null,
            commitUpTo: null,
            abortAfter: null));

        Assert.Equal(0, fixture.Provider.WriteCount);

        var etag = await fixture.Storage.Store(
            loaded.ETag,
            loaded.Metadata,
            statesToPrepare: null,
            commitUpTo: null,
            abortAfter: null);

        Assert.Equal("etag-2", etag);
        Assert.Equal(1, fixture.Provider.WriteCount);
        Assert.Equal(1, fixture.Provider.DurableState.CommittedSequenceId);
        Assert.Equal("committed", fixture.Provider.DurableState.CommittedState.Value);
        Assert.Collection(
            fixture.Provider.DurableState.PendingStates,
            state => AssertPendingState(state, 2, "prepared"));
    }

    [Fact]
    public async Task SuccessfulWritePreservesPrepareAbortAndCommitSemantics()
    {
        var initialState = CreateInitialState();
        initialState.PendingStates.Add(CreatePendingState(4, "replace-me"));
        initialState.PendingStates.Add(CreatePendingState(5, "abort-me"));
        using var fixture = new Fixture(initialState);
        var loaded = await fixture.Storage.Load();

        var etag = await fixture.Storage.Store(
            loaded.ETag,
            CreateMetadata(40),
            [
                CreatePendingState(3, "commit-me"),
                CreatePendingState(4, "replacement"),
            ],
            commitUpTo: 3,
            abortAfter: 4);

        Assert.Equal("etag-2", etag);
        Assert.Equal(3, fixture.Provider.DurableState.CommittedSequenceId);
        Assert.Equal("commit-me", fixture.Provider.DurableState.CommittedState.Value);
        Assert.Equal(40, fixture.Provider.DurableState.Metadata.TimeStamp.Ticks);
        Assert.Collection(
            fixture.Provider.DurableState.PendingStates,
            state => AssertPendingState(state, 4, "replacement"));
    }

    private static TransactionalStateRecord<TestState> CreateInitialState() => new()
    {
        CommittedState = new TestState { Value = "committed" },
        CommittedSequenceId = 1,
        Metadata = CreateMetadata(10),
        PendingStates = [CreatePendingState(2, "prepared")],
    };

    private static TransactionalStateMetaData CreateMetadata(long ticks) => new()
    {
        TimeStamp = new DateTime(ticks),
    };

    private static PendingTransactionState<TestState> CreatePendingState(long sequenceId, string value) => new()
    {
        SequenceId = sequenceId,
        TransactionId = $"transaction-{sequenceId}",
        State = new TestState { Value = value },
    };

    private static void AssertPendingState(PendingTransactionState<TestState> state, long sequenceId, string value)
    {
        Assert.Equal(sequenceId, state.SequenceId);
        Assert.Equal(value, state.State.Value);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly RuntimeContextScope _runtimeContextScope;

        public Fixture(TransactionalStateRecord<TestState> initialState)
        {
            Provider = new TestGrainStorage(initialState, "etag-1");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMetrics();
            services.AddSingleton<OrleansInstruments>();
            services.AddSerializer();
            var storageInstrumentsType = typeof(OrleansInstruments).Assembly.GetType(
                "Orleans.Runtime.StorageInstruments",
                throwOnError: true)!;
            services.AddSingleton(storageInstrumentsType);
            _serviceProvider = services.BuildServiceProvider();

            var context = new TestGrainContext(
                GrainId.Create("transactional-state-storage-test", Guid.NewGuid().ToString("N")),
                _serviceProvider);
            _runtimeContextScope = new RuntimeContextScope(context);
            Storage = new TransactionalStateStorageProviderWrapper<TestState>(Provider, "state", context);
        }

        public TestGrainStorage Provider { get; }

        public TransactionalStateStorageProviderWrapper<TestState> Storage { get; }

        public void Dispose()
        {
            _runtimeContextScope.Dispose();
            _serviceProvider.Dispose();
        }
    }

    private sealed class RuntimeContextScope : IDisposable
    {
        private static readonly Type RuntimeContextType = typeof(IGrainContext).Assembly.GetType(
            "Orleans.Runtime.RuntimeContext",
            throwOnError: true)!;
        private static readonly MethodInfo SetExecutionContext = RuntimeContextType.GetMethod(
            "SetExecutionContext",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        private static readonly MethodInfo ResetExecutionContext = RuntimeContextType.GetMethod(
            "ResetExecutionContext",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        private readonly IGrainContext? _originalContext;

        public RuntimeContextScope(IGrainContext context)
        {
            object?[] arguments = [context, null];
            SetExecutionContext.Invoke(null, arguments);
            _originalContext = (IGrainContext?)arguments[1];
        }

        public void Dispose() => ResetExecutionContext.Invoke(null, [_originalContext]);
    }

    private sealed class TestGrainStorage(
        TransactionalStateRecord<TestState> initialState,
        string initialETag) : IGrainStorage
    {
        private int _etagVersion = 1;

        public TransactionalStateRecord<TestState> DurableState { get; private set; } = Clone(initialState);

        public string DurableETag { get; private set; } = initialETag;

        public bool FailNextWrite { get; set; }

        public bool PersistFailedWrite { get; set; }

        public int WriteCount { get; private set; }

        public TransactionalStateRecord<TestState>? LastAttemptedState { get; private set; }

        public string? LastAttemptedETag { get; private set; }

        public Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            var state = GetTransactionalState(grainState);
            state.State = Clone(DurableState);
            state.ETag = DurableETag;
            state.RecordExists = true;
            return Task.CompletedTask;
        }

        public Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            var state = GetTransactionalState(grainState);
            WriteCount++;
            LastAttemptedState = Clone(state.State!);

            if (state.ETag != DurableETag)
            {
                throw new InvalidOperationException($"Expected ETag '{DurableETag}', received '{state.ETag}'.");
            }

            if (FailNextWrite)
            {
                FailNextWrite = false;
                state.ETag = "speculative-etag";
                LastAttemptedETag = state.ETag;
                if (PersistFailedWrite)
                {
                    DurableState = Clone(state.State!);
                    DurableETag = "etag-ambiguous";
                }

                throw new InvalidOperationException("Injected storage failure.");
            }

            DurableState = Clone(state.State!);
            DurableETag = $"etag-{++_etagVersion}";
            state.ETag = DurableETag;
            LastAttemptedETag = state.ETag;
            return Task.CompletedTask;
        }

        public Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState) =>
            throw new NotSupportedException();

        private static IGrainState<TransactionalStateRecord<TestState>> GetTransactionalState<T>(IGrainState<T> grainState)
        {
            if (grainState is IGrainState<TransactionalStateRecord<TestState>> result)
            {
                return result;
            }

            throw new InvalidOperationException($"Unexpected state type '{typeof(T)}'.");
        }

        private static TransactionalStateRecord<TestState> Clone(TransactionalStateRecord<TestState> value) => new()
        {
            CommittedState = new TestState { Value = value.CommittedState.Value },
            CommittedSequenceId = value.CommittedSequenceId,
            Metadata = new TransactionalStateMetaData
            {
                TimeStamp = value.Metadata.TimeStamp,
                CommitRecords = new Dictionary<Guid, CommitRecord>(value.Metadata.CommitRecords),
            },
            PendingStates = value.PendingStates.Select(state => new PendingTransactionState<TestState>
            {
                SequenceId = state.SequenceId,
                TransactionId = state.TransactionId,
                TimeStamp = state.TimeStamp,
                TransactionManager = state.TransactionManager,
                State = new TestState { Value = state.State.Value },
            }).ToList(),
        };
    }

    private sealed class TestGrainContext(GrainId grainId, IServiceProvider activationServices) : IGrainContext
    {
        public GrainReference GrainReference => throw new NotSupportedException();
        public GrainId GrainId => grainId;
        public object? GrainInstance => null;
        public ActivationId ActivationId => throw new NotSupportedException();
        public GrainAddress Address => throw new NotSupportedException();
        public IServiceProvider ActivationServices => activationServices;
        public IGrainLifecycle ObservableLifecycle => throw new NotSupportedException();
        public IWorkItemScheduler Scheduler => throw new NotSupportedException();
        public Task Deactivated => Task.CompletedTask;

        public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Deactivate(DeactivationReason deactivationReason, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public bool Equals(IGrainContext? other) => ReferenceEquals(this, other);

        public TComponent? GetComponent<TComponent>() where TComponent : class => null;

        public object? GetComponent(Type componentType) => null;

        public TTarget? GetTarget<TTarget>() where TTarget : class => null;

        public object? GetTarget() => null;

        public void Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void ReceiveMessage(object message) => throw new NotSupportedException();

        public void Rehydrate(IRehydrationContext context) => throw new NotSupportedException();

        public void SetComponent<TComponent>(TComponent? value) where TComponent : class =>
            throw new NotSupportedException();
    }

    private sealed class TestState
    {
        public string Value { get; set; } = "";
    }
}
