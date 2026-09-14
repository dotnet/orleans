using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using Cassandra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using Orleans.Persistence.TestKit;
using TestExtensions;
using Xunit;

namespace Orleans.Persistence.Cassandra.Tests;

// These tests use FakeCassandra for deterministic ownership, cancellation, and lifecycle coverage.
// The CassandraPersistenceIntegrationTests class covers behavior which requires a real Cassandra server.
[TestProvider("None"), TestSuite("BVT"), TestCategory("Cassandra"), TestCategory("Persistence")]
public sealed class CassandraGrainStorageTests
{
    [Fact]
    public async Task PersistenceTestKit_WriteReadAndClear()
    {
        var fake = new FakeCassandra();
        using var storage = await CreateStorage(fake);
        var runner = new CassandraTestRunner(storage);

        await runner.PersistenceStorage_WriteRead_StringKeyAsync(TestContext.Current.CancellationToken);
        await runner.PersistenceStorage_WriteClearReadAsync(TestContext.Current.CancellationToken);
        await runner.PersistenceStorage_ClearBeforeWriteAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UuidV7EtagsChangeOnMutationAndLogicalClear()
    {
        var fake = new FakeCassandra();
        using var storage = await CreateStorage(fake, deleteStateOnClear: false);
        var grainId = GrainId.Create("test", "uuid");
        var state = new GrainState<TestState>(new TestState(1));

        await storage.WriteStateAsync("state", grainId, state);
        AssertUuidV7(state.ETag);
        var firstEtag = state.ETag;

        state.State = new TestState(2);
        await storage.WriteStateAsync("state", grainId, state);
        AssertUuidV7(state.ETag);
        Assert.NotEqual(firstEtag, state.ETag);

        var stale = new GrainState<TestState>(new TestState(3)) { ETag = firstEtag };
        await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.WriteStateAsync("state", grainId, stale));

        state.State = new TestState(4);
        await storage.ClearStateAsync("state", grainId, state);
        AssertUuidV7(state.ETag);
        Assert.NotEqual(firstEtag, state.ETag);
        Assert.False(state.RecordExists);

        var read = new GrainState<TestState>(new TestState(99));
        await storage.ReadStateAsync("state", grainId, read);
        Assert.False(read.RecordExists);
        Assert.Equal(state.ETag, read.ETag);
        Assert.Equal(0, Assert.IsType<TestState>(read.State).Value);
    }

    [Fact]
    public async Task EmptyEtagFailsBeforeIssuingWrite()
    {
        var fake = new FakeCassandra();
        using var storage = await CreateStorage(fake);
        var state = new GrainState<TestState>(new TestState(1)) { ETag = Guid.Empty.ToString("D") };
        var executionCount = fake.ExecutionCount;

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.WriteStateAsync("state", GrainId.Create("test", "overflow"), state));

        Assert.Equal(executionCount, fake.ExecutionCount);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("*")]
    public async Task InvalidAndWildcardEtagsAreRejected(string etag)
    {
        var fake = new FakeCassandra();
        using var storage = await CreateStorage(fake);
        var grainId = GrainId.Create("test", "invalid-etag");
        var state = new GrainState<TestState>(new TestState(1)) { ETag = etag };

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.WriteStateAsync("state", grainId, state));
        await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.ClearStateAsync("state", grainId, state));
    }

    [Fact]
    public async Task ConfiguredConsistencyLevelsAreAppliedToCassandraStatements()
    {
        var fake = new FakeCassandra();
        var options = CreateOptions(fake.Session);
        options.ConsistencyLevel = ConsistencyLevel.LocalQuorum;
        options.SerialConsistencyLevel = ConsistencyLevel.LocalSerial;
        var storage = CreateProvider(options, fake);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        storage.Participate(lifecycle);
        await lifecycle.OnStart(TestContext.Current.CancellationToken);
        using (storage)
        {
            var grainId = GrainId.Create("test", "consistency");
            var state = new GrainState<TestState>(new TestState(1));

            await storage.WriteStateAsync("state", grainId, state);
            await storage.ReadStateAsync("state", grainId, new GrainState<TestState>(new()));
            await storage.ClearStateAsync("state", grainId, state);
        }

        Assert.Contains(
            fake.ExecutedStatements,
            statement => statement.Kind == "read"
                && statement.ConsistencyLevel == ConsistencyLevel.LocalQuorum);
        Assert.Contains(
            fake.ExecutedStatements,
            statement => statement.Kind == "insert"
                && statement.ConsistencyLevel == ConsistencyLevel.LocalQuorum
                && statement.SerialConsistencyLevel == ConsistencyLevel.LocalSerial);
        Assert.Contains(
            fake.ExecutedStatements,
            statement => statement.Kind == "clear"
                && statement.ConsistencyLevel == ConsistencyLevel.LocalQuorum
                && statement.SerialConsistencyLevel == ConsistencyLevel.LocalSerial);
    }

    [Fact]
    public async Task PhysicalClearRemovesEtagAndRetainedClearIsIdempotent()
    {
        var fake = new FakeCassandra();
        using var storage = await CreateStorage(fake, deleteStateOnClear: true);
        var grainId = GrainId.Create("test", "clear");
        var state = new GrainState<TestState>(new TestState(1));

        await storage.ClearStateAsync("state", grainId, state);
        Assert.False(state.RecordExists);
        Assert.Null(state.ETag);

        await storage.WriteStateAsync("state", grainId, state);
        await storage.ClearStateAsync("state", grainId, state);
        Assert.Null(state.ETag);

        var read = new GrainState<TestState>(new TestState(9));
        await storage.ReadStateAsync("state", grainId, read);
        Assert.False(read.RecordExists);
        Assert.Null(read.ETag);

        var missing = new GrainState<TestState>(new TestState(8));
        await storage.ClearStateAsync("state", grainId, missing);
        Assert.Null(missing.ETag);
    }

    [Fact]
    public async Task ClearWithoutEtagConflictsWithActiveRow()
    {
        var fake = new FakeCassandra();
        using var storage = await CreateStorage(fake);
        var grainId = GrainId.Create("test", "active-clear");
        var state = new GrainState<TestState>(new TestState(1));
        await storage.WriteStateAsync("state", grainId, state);

        var clear = new GrainState<TestState>(new TestState(2));
        await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.ClearStateAsync("state", grainId, clear));
    }

    [Fact]
    public async Task PhysicalClearWithoutEtagConflictsWithActiveRowAndPreservesIt()
    {
        var fake = new FakeCassandra();
        using var storage = await CreateStorage(fake, deleteStateOnClear: true);
        var grainId = GrainId.Create("test", "physical-active-clear");
        var state = new GrainState<TestState>(new TestState(1));
        await storage.WriteStateAsync("state", grainId, state);

        var clear = new GrainState<TestState>(new TestState(2));
        await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.ClearStateAsync("state", grainId, clear));

        var read = new GrainState<TestState>(new TestState(3));
        await storage.ReadStateAsync("state", grainId, read);
        Assert.True(read.RecordExists);
        Assert.Equal(state.ETag, read.ETag);
        Assert.Equal(1, Assert.IsType<TestState>(read.State).Value);
    }

    [Fact]
    public async Task PhysicalClearWithStaleEtagConflictsAndPreservesActiveRow()
    {
        var fake = new FakeCassandra();
        using var storage = await CreateStorage(fake, deleteStateOnClear: true);
        var grainId = GrainId.Create("test", "physical-stale-clear");
        var state = new GrainState<TestState>(new TestState(1));
        await storage.WriteStateAsync("state", grainId, state);
        var firstEtag = state.ETag;

        state.State = new TestState(2);
        await storage.WriteStateAsync("state", grainId, state);
        var currentEtag = state.ETag;
        var stale = new GrainState<TestState>(new TestState(3)) { ETag = firstEtag };

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.ClearStateAsync("state", grainId, stale));

        var read = new GrainState<TestState>(new TestState(4));
        await storage.ReadStateAsync("state", grainId, read);
        Assert.True(read.RecordExists);
        Assert.Equal(currentEtag, read.ETag);
        Assert.Equal(2, Assert.IsType<TestState>(read.State).Value);
    }

    [Fact]
    public async Task PhysicalDeleteAndRecreateRejectsTheDeletedRowsEtag()
    {
        var fake = new FakeCassandra();
        using var storage = await CreateStorage(fake, deleteStateOnClear: true);
        var grainId = GrainId.Create("test", "recreated");
        var original = new GrainState<TestState>(new TestState(1));

        await storage.WriteStateAsync("state", grainId, original);
        var stale = new GrainState<TestState>(new TestState(2)) { ETag = original.ETag };
        await storage.ClearStateAsync("state", grainId, original);

        var recreated = new GrainState<TestState>(new TestState(3));
        await storage.WriteStateAsync("state", grainId, recreated);
        AssertUuidV7(recreated.ETag);
        Assert.NotEqual(stale.ETag, recreated.ETag);

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.WriteStateAsync("state", grainId, stale));

        var read = new GrainState<TestState>(new());
        await storage.ReadStateAsync("state", grainId, read);
        Assert.Equal(3, Assert.IsType<TestState>(read.State).Value);
        Assert.Equal(recreated.ETag, read.ETag);
    }

    [Fact]
    public async Task ClearWithoutEtagOnMissingRowLeavesNoTombstone()
    {
        var fake = new FakeCassandra();
        using var storage = await CreateStorage(fake);
        var grainId = GrainId.Create("test", "missing-clear");
        var clear = new GrainState<TestState>(new TestState(2));

        await storage.ClearStateAsync("state", grainId, clear);

        Assert.False(clear.RecordExists);
        Assert.Null(clear.ETag);
        await storage.ReadStateAsync("state", grainId, clear);
        Assert.False(clear.RecordExists);
        Assert.Null(clear.ETag);
    }

    [Fact]
    public async Task ClearWithoutEtagPreservesTombstoneEtagAndAllowsNextWrite()
    {
        var fake = new FakeCassandra();
        using var storage = await CreateStorage(fake);
        var grainId = GrainId.Create("test", "tombstone-clear");
        var state = new GrainState<TestState>(new TestState(1));

        await storage.WriteStateAsync("state", grainId, state);
        await storage.ClearStateAsync("state", grainId, state);
        AssertUuidV7(state.ETag);
        var tombstoneEtag = state.ETag;

        var clear = new GrainState<TestState>(new TestState(2));
        await storage.ClearStateAsync("state", grainId, clear);
        Assert.Equal(tombstoneEtag, clear.ETag);

        clear.State = new TestState(3);
        await storage.WriteStateAsync("state", grainId, clear);
        AssertUuidV7(clear.ETag);
        Assert.NotEqual(tombstoneEtag, clear.ETag);
    }

    [Fact]
    public async Task ExternalSessionIsUsableAfterLifecycleStopAndInitializationFailureDoesNotDisposeIt()
    {
        var fake = new FakeCassandra();
        var storage = CreateProvider(CreateOptions(fake.Session), fake);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        storage.Participate(lifecycle);
        await lifecycle.OnStart(TestContext.Current.CancellationToken);
        await lifecycle.OnStop(TestContext.Current.CancellationToken);
        storage.Dispose();

        Assert.Equal(0, fake.ClusterShutdownCount);
        Assert.Equal(0, fake.ClusterDisposeCount);
        Assert.Equal(0, fake.SessionDisposeCount);
        await fake.Session.ExecuteAsync(Substitute.For<IStatement>());

        var failing = new FakeCassandra { FailPrepare = true };
        var options = CreateOptions(failing.Session, ownsSession: true);
        var failedStorage = CreateProvider(options, failing);
        var failingLifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        failedStorage.Participate(failingLifecycle);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failingLifecycle.OnStart(TestContext.Current.CancellationToken));

        Assert.Equal(1, failing.ClusterDisposeCount);
        Assert.True(failing.ClusterDisposeSignal.Task.IsCompletedSuccessfully);
        failedStorage.Dispose();
        Assert.Equal(1, failing.ClusterDisposeCount);
    }

    [Fact]
    public async Task OwnedSessionIsDisposedAfterLifecycleStop()
    {
        var fake = new FakeCassandra();
        var options = CreateOptions(fake.Session, ownsSession: true);
        var storage = CreateProvider(options, fake);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        storage.Participate(lifecycle);
        await lifecycle.OnStart(TestContext.Current.CancellationToken);
        await lifecycle.OnStop(TestContext.Current.CancellationToken);

        Assert.Equal(1, fake.ClusterShutdownCount);
        Assert.Equal(1, fake.ClusterDisposeCount);
    }

    [Fact]
    public async Task ConcurrentCloseAndDisposeDisposeOwnedSessionExactlyOnce()
    {
        var fake = new FakeCassandra();
        var storage = CreateProvider(CreateOptions(fake.Session, ownsSession: true), fake);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        storage.Participate(lifecycle);
        await lifecycle.OnStart(TestContext.Current.CancellationToken);

        using var start = new Barrier(2);
        var close = Task.Run(async () =>
        {
            start.SignalAndWait(TestContext.Current.CancellationToken);
            await lifecycle.OnStop(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);
        var dispose = Task.Run(() =>
        {
            start.SignalAndWait(TestContext.Current.CancellationToken);
            storage.Dispose();
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(close, dispose);

        Assert.InRange(fake.ClusterShutdownCount, 0, 1);
        Assert.Equal(1, fake.ClusterDisposeCount);
        Assert.Equal(0, fake.SessionDisposeCount);
    }

    [Fact]
    public async Task CloseAndDisposeWaitForInitializationAndDisposeUnpublishedOwnedSessionExactlyOnce()
    {
        var fake = new FakeCassandra();
        var initializationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInitialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new CassandraGrainStorageOptions
        {
            TableName = "grain_state",
            GrainStorageSerializer = new JsonStorageSerializer(),
        };
        options.ConfigureClient(
            async _ =>
            {
                initializationStarted.SetResult();
                await releaseInitialization.Task;
                return fake.Session;
            },
            ownsSession: true);

        using var closePathsEntered = new Barrier(3);
        var storage = CreateProvider(options, fake, () => closePathsEntered.SignalAndWait(TestContext.Current.CancellationToken));
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        storage.Participate(lifecycle);
        var start = lifecycle.OnStart(TestContext.Current.CancellationToken);
        await initializationStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var stop = Task.Run(
            () => lifecycle.OnStop(TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        var dispose = Task.Run(storage.Dispose, TestContext.Current.CancellationToken);
        closePathsEntered.SignalAndWait(TestContext.Current.CancellationToken);

        releaseInitialization.SetResult();
        await Task.WhenAll(start, stop, dispose);

        Assert.Equal(1, fake.ClusterDisposeCount);
        Assert.Equal(0, fake.SessionDisposeCount);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.ReadStateAsync("state", GrainId.Create("test", "not-published"), new GrainState<TestState>(new())));
    }

    [Fact]
    public async Task CancellationDoesNotCompleteReadWriteOrClear()
    {
        var fake = new FakeCassandra();
        using var storage = await CreateStorage(fake);
        var grainId = GrainId.Create("test", "cancel");
        var state = new GrainState<TestState>(new TestState(1));

        using var readCancellation = new CancellationTokenSource();
        fake.BlockExecution();
        var read = storage.ReadStateAsync("state", grainId, state, readCancellation.Token);
        await fake.ExecutionStarted.Task;
        readCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        fake.ReleaseExecution();
        await fake.ExecutionCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);

        using var writeCancellation = new CancellationTokenSource();
        fake.BlockExecution();
        var write = storage.WriteStateAsync("state", grainId, state, writeCancellation.Token);
        await fake.ExecutionStarted.Task;
        writeCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        fake.ReleaseExecution();
        await fake.ExecutionCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);

        using var clearCancellation = new CancellationTokenSource();
        fake.BlockExecution();
        var clear = storage.ClearStateAsync("state", grainId, state, clearCancellation.Token);
        await fake.ExecutionStarted.Task;
        clearCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clear);
        fake.ReleaseExecution();
        await fake.ExecutionCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MissingStateUsesOrleansActivatorForReset()
    {
        var fake = new FakeCassandra();
        using var storage = await CreateStorage(fake);
        var state = new GrainState<ActivatedState>(new ActivatedState(new TestDependency()));

        await storage.ReadStateAsync("state", GrainId.Create("test", "activation"), state);

        Assert.False(state.RecordExists);
        Assert.Equal(7, Assert.IsType<ActivatedState>(state.State).Value);
    }

    [Fact]
    public async Task ShutdownDrainsInFlightOperationAndRejectsNewOperations()
    {
        var fake = new FakeCassandra();
        var options = CreateOptions(fake.Session);
        var storage = CreateProvider(options, fake);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        storage.Participate(lifecycle);
        await lifecycle.OnStart(TestContext.Current.CancellationToken);

        fake.BlockExecution();
        var state = new GrainState<TestState>(new TestState());
        var operation = storage.ReadStateAsync("state", GrainId.Create("test", "drain"), state);
        await fake.ExecutionStarted.Task;

        var stop = lifecycle.OnStop(TestContext.Current.CancellationToken);
        Assert.False(stop.IsCompleted);
        fake.ReleaseExecution();
        await operation;
        await stop;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.ReadStateAsync("state", GrainId.Create("test", "after-stop"), new GrainState<TestState>(new())));
        storage.Dispose();
    }

    [Fact]
    public async Task ShutdownAndDisposeWaitForCanceledUnderlyingOperation()
    {
        var fake = new FakeCassandra();
        var storage = CreateProvider(CreateOptions(fake.Session, ownsSession: true), fake);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        storage.Participate(lifecycle);
        await lifecycle.OnStart(TestContext.Current.CancellationToken);

        fake.BlockExecution();
        using var cancellation = new CancellationTokenSource();
        var operation = storage.ReadStateAsync("state", GrainId.Create("test", "cancel-drain"), new GrainState<TestState>(), cancellation.Token);
        await fake.ExecutionStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        var stop = lifecycle.OnStop(TestContext.Current.CancellationToken);
        Assert.False(stop.IsCompleted);
        Assert.False(fake.ClusterDisposeSignal.Task.IsCompleted);

        fake.ReleaseExecution();
        await fake.ExecutionCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await stop.WaitAsync(TestContext.Current.CancellationToken);
        await fake.ClusterDisposeSignal.Task.WaitAsync(TestContext.Current.CancellationToken);
        storage.Dispose();
        Assert.Equal(1, fake.ClusterDisposeCount);
    }

    [Fact]
    public async Task CanceledCloseContinuesOwnedClusterDisposal()
    {
        var fake = new FakeCassandra();
        var storage = await CreateStorage(fake, ownsSession: true);
        fake.BlockExecution();
        var operation = storage.ReadStateAsync("state", GrainId.Create("test", "cancel-close"), new GrainState<TestState>());
        await fake.ExecutionStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.CloseAsync(cancellation.Token));
        Assert.False(fake.ClusterDisposeSignal.Task.IsCompleted);

        fake.ReleaseExecution();
        await fake.ExecutionCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await fake.ClusterDisposeSignal.Task.WaitAsync(TestContext.Current.CancellationToken);
        await operation;
        storage.Dispose();

        Assert.Equal(1, fake.ClusterDisposeCount);
    }

    [Fact]
    public async Task CanceledLifecycleStartDoesNotInitialize()
    {
        var fake = new FakeCassandra();
        var storage = CreateProvider(CreateOptions(fake.Session), fake);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        storage.Participate(lifecycle);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OrleansLifecycleCanceledException>(
            () => lifecycle.OnStart(cancellation.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.ReadStateAsync("state", GrainId.Create("test", "not-started"), new GrainState<TestState>(new())));
        storage.Dispose();
    }

    [Fact]
    public async Task CanceledLifecycleStartDefersOwnedClusterDisposalUntilPendingPrepareCompletes()
    {
        var fake = new FakeCassandra();
        using var cancellation = new CancellationTokenSource();
        fake.BlockPrepare(cancellation.Cancel);
        var storage = CreateProvider(CreateOptions(fake.Session, ownsSession: true), fake);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        storage.Participate(lifecycle);

        var start = lifecycle.OnStart(cancellation.Token);
        await fake.PrepareStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);

        Assert.Equal(0, fake.ClusterDisposeCount);

        fake.ReleasePrepare();
        await fake.PrepareCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await fake.ClusterDisposeSignal.Task.WaitAsync(TestContext.Current.CancellationToken);
        storage.Dispose();

        Assert.Equal(1, fake.ClusterDisposeCount);
    }

    [Fact]
    public async Task InitializationCancellationPreventsStartingTheNextPrepare()
    {
        var fake = new FakeCassandra();
        using var cancellation = new CancellationTokenSource();
        fake.CancelAfterFirstPrepare(cancellation);
        var storage = CreateProvider(CreateOptions(fake.Session, ownsSession: true), fake);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        storage.Participate(lifecycle);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lifecycle.OnStart(cancellation.Token));

        Assert.Equal(1, fake.PrepareInvocationCount);
        await fake.ClusterDisposeSignal.Task.WaitAsync(TestContext.Current.CancellationToken);
        storage.Dispose();
    }

    private static async Task<CassandraGrainStorage> CreateStorage(
        FakeCassandra fake,
        bool deleteStateOnClear = false,
        bool ownsSession = false)
    {
        var storage = CreateProvider(CreateOptions(fake.Session, deleteStateOnClear, ownsSession), fake);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        storage.Participate(lifecycle);
        await lifecycle.OnStart(TestContext.Current.CancellationToken);
        return storage;
    }

    private static CassandraGrainStorageOptions CreateOptions(ISession session, bool deleteStateOnClear = false, bool ownsSession = false)
    {
        var options = new CassandraGrainStorageOptions
        {
            DeleteStateOnClear = deleteStateOnClear,
            TableName = "grain_state",
            GrainStorageSerializer = new JsonStorageSerializer(),
        };
        options.ConfigureClient(_ => Task.FromResult(session), ownsSession);
        return options;
    }

    private static CassandraGrainStorage CreateProvider(
        CassandraGrainStorageOptions options,
        FakeCassandra fake,
        Action? closePathEntered = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<TestDependency>()
            .BuildServiceProvider();
        return new CassandraGrainStorage(
            "Cassandra",
            options,
            Options.Create(new ClusterOptions { ServiceId = "cassandra-tests" }),
            new TestActivatorProvider(services),
            options.GrainStorageSerializer,
            NullLogger<CassandraGrainStorage>.Instance,
            services,
            fake.ExecuteAsync,
            closePathEntered);
    }

    private static void AssertUuidV7(string? etag)
    {
        Assert.True(Guid.TryParse(etag, out var value));
        Assert.NotEqual(Guid.Empty, value);
        Assert.Equal('7', value.ToString("D")[14]);
    }

    private sealed class TestState
    {
        public TestState() { }
        public TestState(int value) => Value = value;
        public int Value { get; set; }
    }

    private sealed class ActivatedState
    {
        public ActivatedState(TestDependency dependency) => Value = dependency.Value;
        public int Value { get; }
    }

    private sealed class TestDependency
    {
        public int Value => 7;
    }

    private sealed class TestActivatorProvider(IServiceProvider services) : IActivatorProvider
    {
        public IActivator<T> GetActivator<T>() => new TestActivator<T>(services);
    }

    private sealed class TestActivator<T>(IServiceProvider services) : IActivator<T>
    {
        public T Create() => ActivatorUtilities.CreateInstance<T>(services);
    }

    private sealed class CassandraTestRunner(IGrainStorage storage) : GrainStorageTestRunner(storage);

    private sealed class JsonStorageSerializer : IGrainStorageSerializer
    {
        public BinaryData Serialize<T>(T? input) => BinaryData.FromString(JsonSerializer.Serialize(input));
        public T? Deserialize<T>(BinaryData input) => JsonSerializer.Deserialize<T>(input.ToString());
    }

    private sealed class FakeCassandra
    {
        private readonly ConcurrentDictionary<PreparedStatement, string> _statements = new();
        private readonly Dictionary<RowKey, StoredRow> _rows = [];
        private readonly object _lock = new();
        private TaskCompletionSource _executionGate = Completed();
        private TaskCompletionSource? _prepareGate;
        private Action? _prepareStartedAction;

        public FakeCassandra()
        {
            var cluster = Substitute.For<ICluster>();
            Cluster = InstrumentedCluster.Create(cluster);
            var session = Substitute.For<ISession>();
            session.Cluster.Returns(Cluster);
            session.PrepareAsync(Arg.Any<string>()).Returns(callInfo =>
            {
                if (FailPrepare) throw new InvalidOperationException("prepare failed");
                PrepareInvocationCount++;
                var statement = Substitute.For<PreparedStatement>();
                _statements[statement] = GetKind(callInfo.Arg<string>());
                statement.Bind(Arg.Any<object[]>()).Returns(call => new TestBoundStatement(
                    statement,
                    call.Arg<object[]>()));
                PrepareStarted.TrySetResult();
                _prepareStartedAction?.Invoke();
                return _prepareGate is null ? Task.FromResult(statement) : CompletePrepareAsync(statement);
            });
            session.ExecuteAsync(Arg.Any<IStatement>()).Returns(Substitute.For<RowSet>());
            Session = InstrumentedSession.Create(session);
        }

        public ISession Session { get; }
        public ICluster Cluster { get; }
        public int ClusterShutdownCount => InstrumentedCluster.Get(Cluster).ShutdownAttempts;
        public int ClusterDisposeCount => InstrumentedCluster.Get(Cluster).DisposeAttempts;
        public int SessionDisposeCount => InstrumentedSession.Get(Session).DisposeAttempts;
        public TaskCompletionSource ClusterDisposeSignal => InstrumentedCluster.Get(Cluster).DisposeSignal;
        public Func<IStatement, Task<CassandraGrainStorage.CassandraResult>> ExecuteAsync => ExecuteAsyncCore;
        public bool FailPrepare { get; init; }
        public TaskCompletionSource ExecutionStarted { get; private set; } = NewSource();
        public TaskCompletionSource ExecutionCompleted { get; private set; } = Completed();
        public TaskCompletionSource PrepareStarted { get; private set; } = NewSource();
        public TaskCompletionSource PrepareCompleted { get; private set; } = Completed();
        public int ExecutionCount { get; private set; }
        public int PrepareInvocationCount { get; private set; }
        public ConcurrentQueue<(string Kind, ConsistencyLevel? ConsistencyLevel, ConsistencyLevel SerialConsistencyLevel)> ExecutedStatements { get; } = [];

        public void BlockExecution()
        {
            _executionGate = NewSource();
            ExecutionStarted = NewSource();
            ExecutionCompleted = NewSource();
        }
        public void ReleaseExecution() => _executionGate.TrySetResult();
        public void BlockPrepare(Action onStarted)
        {
            _prepareGate = NewSource();
            _prepareStartedAction = onStarted;
            PrepareStarted = NewSource();
            PrepareCompleted = NewSource();
        }
        public void ReleasePrepare() => _prepareGate!.TrySetResult();
        public void CancelAfterFirstPrepare(CancellationTokenSource cancellation)
        {
            var first = true;
            _prepareStartedAction = () =>
            {
                if (first)
                {
                    first = false;
                    cancellation.Cancel();
                }
            };
        }

        private async Task<CassandraGrainStorage.CassandraResult> ExecuteAsyncCore(IStatement statement)
        {
            ExecutionCount++;
            ExecutionStarted.TrySetResult();
            try
            {
                await _executionGate.Task;
                if (statement is not TestBoundStatement bound || !_statements.TryGetValue(bound.PreparedStatement, out var kind))
                {
                    return default;
                }

                ExecutedStatements.Enqueue((kind, statement.ConsistencyLevel, statement.SerialConsistencyLevel));
                var values = bound.QueryValues;
                lock (_lock)
                {
                    switch (kind)
                    {
                        case "read":
                            return Read(new RowKey((string)values[0], (string)values[1], (string)values[2]));
                        case "insert":
                        {
                            var key = new RowKey((string)values[0], (string)values[1], (string)values[2]);
                            if (_rows.ContainsKey(key)) return Applied(false);
                            _rows[key] = new StoredRow((Guid)values[4], true, (byte[])values[5]);
                            return Applied(true);
                        }
                        case "update":
                            return Update(new RowKey((string)values[4], (string)values[5], (string)values[6]), (Guid)values[7], (Guid)values[1], (byte[])values[2]);
                        case "clear":
                            return UpdateClear(new RowKey((string)values[2], (string)values[3], (string)values[4]), (Guid)values[5], (Guid)values[0]);
                        case "delete":
                        {
                            var key = new RowKey((string)values[0], (string)values[1], (string)values[2]);
                            if (!_rows.TryGetValue(key, out var existing) || existing.ETag != (Guid)values[3]) return Applied(false);
                            _rows.Remove(key);
                            return Applied(true);
                        }
                        case "delete-without-etag":
                        {
                            var key = new RowKey((string)values[0], (string)values[1], (string)values[2]);
                            if (!_rows.TryGetValue(key, out var row)) return Applied(true);
                            if (row.RecordExists) return Applied(false, row);
                            _rows.Remove(key);
                            return Applied(true);
                        }
                        default:
                            return default;
                    }
                }
            }
            finally
            {
                ExecutionCompleted.TrySetResult();
            }
        }

        private async Task<PreparedStatement> CompletePrepareAsync(PreparedStatement statement)
        {
            try
            {
                await _prepareGate!.Task;
                return statement;
            }
            finally
            {
                PrepareCompleted.TrySetResult();
            }
        }

        private CassandraGrainStorage.CassandraResult Read(RowKey key) =>
            _rows.TryGetValue(key, out var row)
                ? new(false, row.RecordExists, row.ETag, row.State)
                : default;

        private CassandraGrainStorage.CassandraResult Update(RowKey key, Guid expected, Guid next, byte[] state)
        {
            if (!_rows.TryGetValue(key, out var row) || row.ETag != expected) return Applied(false);
            _rows[key] = new StoredRow(next, true, state);
            return Applied(true);
        }

        private CassandraGrainStorage.CassandraResult UpdateClear(RowKey key, Guid expected, Guid next)
        {
            if (!_rows.TryGetValue(key, out var row) || row.ETag != expected) return Applied(false);
            _rows[key] = new StoredRow(next, false, null);
            return Applied(true);
        }

        private static string GetKind(string cql) =>
            cql.StartsWith("SELECT", StringComparison.Ordinal) ? "read" :
            cql.StartsWith("INSERT", StringComparison.Ordinal) ? "insert" :
            cql.StartsWith("UPDATE", StringComparison.Ordinal) && cql.Contains("record_exists = true", StringComparison.Ordinal) ? "update" :
            cql.StartsWith("UPDATE", StringComparison.Ordinal) ? "clear" :
            cql.Contains("IF record_exists = false", StringComparison.Ordinal) ? "delete-without-etag" :
            "delete";

        private static CassandraGrainStorage.CassandraResult Applied(bool applied, StoredRow? row = null) =>
            new(applied, row?.RecordExists, null, null);

        private static TaskCompletionSource NewSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static TaskCompletionSource Completed()
        {
            var source = NewSource();
            source.SetResult();
            return source;
        }

        private sealed record RowKey(string ServiceId, string GrainId, string StateName);
        private sealed record StoredRow(Guid ETag, bool RecordExists, byte[]? State);

        private class InstrumentedCluster : DispatchProxy
        {
            private ICluster _inner = default!;
            private int _shutdownAttempts;
            private int _disposeAttempts;

            public int ShutdownAttempts => Volatile.Read(ref _shutdownAttempts);
            public int DisposeAttempts => Volatile.Read(ref _disposeAttempts);
            public TaskCompletionSource DisposeSignal { get; } = NewSource();

            public static ICluster Create(ICluster inner)
            {
                var proxy = (InstrumentedCluster)(object)Create<ICluster, InstrumentedCluster>();
                proxy._inner = inner;
                return (ICluster)(object)proxy;
            }

            public static InstrumentedCluster Get(ICluster cluster) => (InstrumentedCluster)(object)cluster;

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod is null) throw new ArgumentNullException(nameof(targetMethod));
                if (targetMethod.Name == nameof(IDisposable.Dispose))
                {
                    Interlocked.Increment(ref _disposeAttempts);
                    DisposeSignal.TrySetResult();
                }
                else if (targetMethod.Name is nameof(ICluster.Shutdown) or nameof(ICluster.ShutdownAsync))
                {
                    Interlocked.Increment(ref _shutdownAttempts);
                }

                try
                {
                    try
                    {
                        return targetMethod.Invoke(_inner, args);
                    }
                    catch (TargetInvocationException exception) when (exception.InnerException is not null)
                    {
                        ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                        throw;
                    }
                }
                catch (TargetInvocationException exception) when (exception.InnerException is not null)
                {
                    ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                    throw;
                }
            }
        }

        internal class InstrumentedSession : DispatchProxy
        {
            private ISession _inner = default!;

            public int DisposeAttempts { get; private set; }

            public static ISession Create(ISession inner)
            {
                var proxy = (InstrumentedSession)(object)Create<ISession, InstrumentedSession>();
                proxy._inner = inner;
                return (ISession)(object)proxy;
            }

            public static InstrumentedSession Get(ISession session) => (InstrumentedSession)(object)session;

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod is null) throw new ArgumentNullException(nameof(targetMethod));
                if (targetMethod.Name == nameof(IDisposable.Dispose))
                {
                    DisposeAttempts++;
                    return null;
                }

                try
                {
                    return targetMethod.Invoke(_inner, args);
                }
                catch (TargetInvocationException exception) when (exception.InnerException is not null)
                {
                    ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                    throw;
                }
            }
        }
    }

    private sealed class TestBoundStatement(PreparedStatement prepared, object[] values) : BoundStatement(prepared)
    {
        public override object[] QueryValues => values;
    }

}
