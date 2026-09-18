using Orleans.Runtime;
using Xunit;

namespace Orleans.Clustering.TestKit.Tests;

public sealed class MembershipTableCleanupLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_SynchronouslyBlockedCallbackStillBoundsCallerWait(bool blockDisposer)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var backend = new IdealizedMembershipBackend();
        var callbackEntered = Gate();
        var releaseCallback = Gate();
        var invocationReturned = Gate();
        var deletes = 0;
        var disposers = 0;
        void BlockCallback()
        {
            callbackEntered.TrySetResult();
            releaseCallback.Task.WaitAsync(ct).GetAwaiter().GetResult();
        }
        var fixture = new MembershipTableTestFixture("synchronous-cleanup", (_, cluster, _) =>
            ValueTask.FromResult(new MembershipTableTestHandle(new LegacyTable(backend.Create(cluster), () =>
            {
                if (Interlocked.Increment(ref deletes) == 1 && !blockDisposer) BlockCallback();
                return Task.CompletedTask;
            }), () =>
            {
                if (Interlocked.Increment(ref disposers) == 1 && blockDisposer) BlockCallback();
                return backend.DisposeHandleAsync(cluster);
            })), backend.IsDeletedAsync);
        await fixture.InitializeAsync(ct);
        var caller = Task.Run(async () =>
        {
            var operation = fixture.DisposeAsync(TimeSpan.Zero).AsTask();
            invocationReturned.TrySetResult();
            return await Assert.ThrowsAsync<TimeoutException>(() => operation);
        }, ct);
        try
        {
            await callbackEntered.Task.WaitAsync(ct);
            await invocationReturned.Task.WaitAsync(ct);
            var failure = await caller.WaitAsync(ct);
            var completion = Assert.IsAssignableFrom<Task>(failure.Data[ClusteringTestKitDiagnostics.CleanupCompletionKey]);
            Assert.False(completion.IsCompleted);
            Assert.Equal(0, backend.DisposedHandles);
            Assert.Equal(blockDisposer ? 2 : 1, Volatile.Read(ref deletes));
        }
        finally
        {
            releaseCallback.TrySetResult();
            await fixture.DisposeAsync();
        }
        await caller;
        Assert.Equal(2, deletes);
        Assert.Equal(3, disposers);
        Assert.Equal(3, backend.DisposedHandles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_TimedOutLegacyDeleteRetainsOwnersAndRepeatedCallsJoinCompletion(bool failDelete)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var backend = new IdealizedMembershipBackend();
        var deleteStarted = Gate();
        var releaseDelete = Gate();
        var expected = new InvalidOperationException("legacy-delete-failed");
        var calls = 0;
        var disposed = 0;
        var fixture = new MembershipTableTestFixture("legacy-cleanup", (_, cluster, _) =>
        {
            var table = new LegacyTable(backend.Create(cluster), async () =>
            {
                var current = Interlocked.Increment(ref calls);
                if (current == 1)
                {
                    deleteStarted.TrySetResult();
                    await releaseDelete.Task.WaitAsync(ct);
                    Assert.Equal(0, Volatile.Read(ref disposed));
                    if (failDelete) throw expected;
                }
            });
            return ValueTask.FromResult(new MembershipTableTestHandle(table, () =>
            {
                Assert.Equal(2, Volatile.Read(ref calls));
                Interlocked.Increment(ref disposed);
                return backend.DisposeHandleAsync(cluster);
            }));
        }, backend.IsDeletedAsync);
        await fixture.InitializeAsync(ct);

        var first = fixture.DisposeAsync(TimeSpan.Zero).AsTask();
        await deleteStarted.Task.WaitAsync(ct);
        var firstTimeout = await Assert.ThrowsAsync<TimeoutException>(() => first);
        var completion = Assert.IsAssignableFrom<Task>(firstTimeout.Data[ClusteringTestKitDiagnostics.CleanupCompletionKey]);
        var secondTimeout = await Assert.ThrowsAsync<TimeoutException>(() => fixture.DisposeAsync(TimeSpan.Zero).AsTask());
        Assert.Same(completion, secondTimeout.Data[ClusteringTestKitDiagnostics.CleanupCompletionKey]);
        Assert.False(completion.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref disposed));
        Assert.Equal(1, Volatile.Read(ref calls));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.CreateAdditionalHandleAsync(fixture.ClusterId, ct).AsTask());
        var join = fixture.DisposeAsync().AsTask();
        Assert.False(join.IsCompleted);
        releaseDelete.TrySetResult();

        if (failDelete)
        {
            var failure = await Assert.ThrowsAsync<AggregateException>(() => join.WaitAsync(ct));
            Assert.Same(expected, Assert.Single(failure.InnerExceptions).InnerException);
            var repeated = await Assert.ThrowsAsync<AggregateException>(() => fixture.DisposeAsync().AsTask());
            Assert.Same(failure, repeated);
            Assert.True(completion.IsFaulted);
        }
        else
        {
            await join.WaitAsync(ct);
            await fixture.DisposeAsync();
            Assert.True(completion.IsCompletedSuccessfully);
        }
        Assert.Equal(2, calls);
        Assert.Equal(3, disposed);
        Assert.Equal(3, backend.DisposedHandles);
    }

    [Fact]
    public async Task Dispose_TimedOutDisposerCompletesOnceAndPreservesItsLateFailure()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var backend = new IdealizedMembershipBackend();
        var disposerStarted = Gate();
        var releaseDisposer = Gate();
        var expected = new InvalidOperationException("late-disposer-failed");
        var disposeCalls = 0;
        var fixture = new MembershipTableTestFixture("late-disposer", (_, cluster, _) =>
            ValueTask.FromResult(new MembershipTableTestHandle(backend.Create(cluster), async () =>
            {
                if (Interlocked.Increment(ref disposeCalls) == 1)
                {
                    disposerStarted.TrySetResult();
                    await releaseDisposer.Task.WaitAsync(ct);
                    throw expected;
                }
                await backend.DisposeHandleAsync(cluster);
            })), backend.IsDeletedAsync);
        await fixture.InitializeAsync(ct);
        var disposal = fixture.DisposeAsync(TimeSpan.Zero).AsTask();
        await disposerStarted.Task.WaitAsync(ct);
        var failure = await Assert.ThrowsAsync<TimeoutException>(() => disposal);
        var completion = Assert.IsAssignableFrom<Task>(failure.Data[ClusteringTestKitDiagnostics.CleanupCompletionKey]);
        Assert.Equal(1, disposeCalls);
        Assert.Equal(2, backend.Deletes);
        Assert.False(completion.IsCompleted);
        var join = fixture.DisposeAsync().AsTask();
        releaseDisposer.TrySetResult();

        var late = await Assert.ThrowsAsync<AggregateException>(() => join.WaitAsync(ct));
        Assert.Same(expected, Assert.Single(late.InnerExceptions));
        Assert.Same(late, await Assert.ThrowsAsync<AggregateException>(() => fixture.DisposeAsync().AsTask()));
        Assert.Equal(3, disposeCalls);
        Assert.Equal(2, backend.Deletes);
        Assert.True(completion.IsFaulted);
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalDelete_CancelledCallerRetainsOwnerThroughNativeDeleteAndProbe(bool pauseProbe)
    {
        var backend = new IdealizedMembershipBackend();
        var entered = Gate();
        var release = Gate();
        using var caller = new CancellationTokenSource();
        var ct = TestContext.Current.CancellationToken;
        var probes = 0;
        var fixture = new MembershipTableTestFixture("terminal-operation", (_, cluster, _) =>
            ValueTask.FromResult(new MembershipTableTestHandle(new LegacyTable(backend.Create(cluster), async () =>
            {
                if (!pauseProbe && backend.Deletes == 0)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                    Assert.Equal(0, backend.DisposedHandles);
                }
            }), () => backend.DisposeHandleAsync(cluster))),
            async (cluster, _) =>
            {
                if (Interlocked.Increment(ref probes) == 2 && pauseProbe)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                    Assert.Equal(0, backend.DisposedHandles);
                }
                return await backend.IsDeletedAsync(cluster, ct);
            });
        await fixture.InitializeAsync(ct);
        await MembershipTableTestRunner.Insert(fixture.First, MembershipTableTestData.CreateEntry(1), ct);
        var deletion = fixture.DeleteClusterAsync(fixture.First, fixture.ClusterId, false, caller.Token);
        await entered.Task.WaitAsync(ct);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deletion);
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.DisposeAsync(TimeSpan.Zero).AsTask());
        Assert.Equal(0, backend.DisposedHandles);
        release.TrySetResult();
        await fixture.DisposeAsync().AsTask().WaitAsync(ct);
        Assert.Equal(2, probes);
        Assert.Equal(2, backend.Deletes);
        Assert.Equal(3, backend.DisposedHandles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedRunner_TeardownFailureStopsFurtherFactoriesAndReplay(bool failCase)
    {
        var backend = new IdealizedMembershipBackend();
        var control = new MembershipFaultController(MembershipFault.VersionJump) { Backend = backend };
        var expected = new TimeoutException("fixture teardown timeout");
        var factories = 0;
        var firstFailureFactory = 0;
        var nativeCallsAfterFailure = 0;
        var failed = false;
        var runner = new MembershipTableModelBasedTestRunner(() =>
        {
            factories++;
            if (failed) nativeCallsAfterFailure++;
            return new MembershipTableTestFixture("fail-stop", (_, cluster, _) =>
            {
                if (failed) nativeCallsAfterFailure++;
                IMembershipTable table = failCase
                    ? new FaultyMembershipTable(control, cluster, backend.Create(cluster))
                    : backend.Create(cluster);
                return ValueTask.FromResult(new MembershipTableTestHandle(table, () =>
                {
                    if (!failed && (!failCase || control.Injected > 0))
                    {
                        failed = true;
                        firstFailureFactory = factories;
                        throw expected;
                    }
                    return backend.DisposeHandleAsync(cluster);
                }));
            }, backend.IsDeletedAsync);
        }, "fail-stop");
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => runner.RunGeneratedConformanceTests(TestContext.Current.CancellationToken));
        Assert.True(failed);
        Assert.Equal(firstFailureFactory, factories);
        Assert.Equal(0, nativeCallsAfterFailure);
        if (failCase)
        {
            Assert.Contains("commit integer", failure.Message);
            Assert.IsType<AggregateException>(failure.Data[ClusteringTestKitDiagnostics.CleanupFailureKey]);
        }
        else Assert.Contains("Membership fixture teardown failed", failure.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Dispose_WaitsForRealLegacyInitializationAfterCancellationOrConcurrentDisposal(bool cancelCaller, bool failInitialization)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        using var caller = new CancellationTokenSource();
        var backend = new IdealizedMembershipBackend();
        var initializationStarted = Gate();
        var releaseInitialization = Gate();
        var expected = new InvalidOperationException("late-legacy-initialization");
        var initialized = 0;
        var fixture = new MembershipTableTestFixture("legacy-initialization", (_, cluster, _) =>
            ValueTask.FromResult(new MembershipTableTestHandle(
                new LegacyTable(backend.Create(cluster), () => Task.CompletedTask, async () =>
                {
                    Interlocked.Increment(ref initialized);
                    initializationStarted.TrySetResult();
                    await releaseInitialization.Task.WaitAsync(ct);
                    Assert.Equal(0, backend.Deletes);
                    Assert.Equal(0, backend.DisposedHandles);
                    if (failInitialization) throw expected;
                }),
                () => backend.DisposeHandleAsync(cluster))), backend.IsDeletedAsync);
        var initialization = fixture.InitializeAsync(caller.Token).AsTask();
        await initializationStarted.Task.WaitAsync(ct);
        if (cancelCaller)
        {
            caller.Cancel();
            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization.WaitAsync(ct));
            Assert.Equal(caller.Token, canceled.CancellationToken);
        }
        var timedOut = await Assert.ThrowsAsync<TimeoutException>(() => fixture.DisposeAsync(TimeSpan.Zero).AsTask());
        var completion = Assert.IsAssignableFrom<Task>(timedOut.Data[ClusteringTestKitDiagnostics.CleanupCompletionKey]);
        Assert.False(completion.IsCompleted);
        Assert.Equal(0, backend.Deletes);
        Assert.Equal(0, backend.DisposedHandles);
        releaseInitialization.TrySetResult();

        if (failInitialization)
        {
            var cleanup = await Assert.ThrowsAsync<AggregateException>(() => fixture.DisposeAsync().AsTask().WaitAsync(ct));
            Assert.Same(expected, Assert.Single(cleanup.InnerExceptions));
        }
        else await fixture.DisposeAsync().AsTask().WaitAsync(ct);
        if (!cancelCaller)
        {
            if (failInitialization)
                Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => initialization.WaitAsync(ct)));
            else
                await Assert.ThrowsAsync<ObjectDisposedException>(() => initialization.WaitAsync(ct));
        }
        Assert.Equal(1, initialized);
        Assert.Equal(3, backend.CreatedHandles);
        Assert.Equal(3, backend.DisposedHandles);
        Assert.Equal(2, backend.Deletes);
    }

    // Exercises the interface's tokenless compatibility dispatch, which cancels waits separately from operations.
    private sealed class LegacyTable(IdealizedMembershipTable inner, Func<Task> beforeDelete, Func<Task>? beforeInitialize = null) : IMembershipTable
    {
        public async Task InitializeMembershipTable(bool tryInitTableVersion)
        {
            if (beforeInitialize is not null) await beforeInitialize();
            await inner.InitializeMembershipTableAsync(tryInitTableVersion);
        }
        public async Task DeleteMembershipTableEntries(string clusterId)
        {
            await beforeDelete();
            await inner.DeleteMembershipTableEntriesAsync(clusterId);
        }
        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => inner.CleanupDefunctSiloEntriesAsync(beforeDate);
        public Task<MembershipTableData> ReadAll() => inner.ReadAllAsync();
        public Task<MembershipTableData> ReadRow(SiloAddress key) => inner.ReadRowAsync(key);
        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => inner.InsertRowAsync(entry, tableVersion);
        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => inner.UpdateRowAsync(entry, etag, tableVersion);
        public Task UpdateIAmAlive(MembershipEntry entry) => inner.UpdateIAmAliveAsync(entry);
    }
}
