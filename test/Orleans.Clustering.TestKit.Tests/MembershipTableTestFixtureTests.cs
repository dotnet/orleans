using Xunit;

namespace Orleans.Clustering.TestKit.Tests;

public sealed class MembershipTableTestFixtureTests
{
    [Fact]
    public async Task Initialize_UsesOneServiceTwoClustersAndThreeDistinctHandles()
    {
        var backend = new IdealizedMembershipBackend();
        var calls = new List<(string Service, string Cluster)>();
        var fixture = new MembershipTableTestFixture("factory", (service, cluster, _) =>
        {
            calls.Add((service, cluster));
            return ValueTask.FromResult(new MembershipTableTestHandle(backend.Create(cluster)));
        }, backend.IsDeletedAsync, "shared-service");
        await fixture.RunAsync((f, _) =>
        {
            Assert.NotSame(f.First, f.Second);
            Assert.NotSame(f.First, f.OtherCluster);
            Assert.Equal(new[] { (f.ServiceId, f.ClusterId), (f.ServiceId, f.ClusterId), (f.ServiceId, f.OtherClusterId) }, calls);
            Assert.Equal("shared-service", f.ServiceId);
            Assert.Equal(f.ClusterId + "-other", f.OtherClusterId);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        Assert.Equal(2, backend.Deletes);
    }

    [Fact]
    public async Task Initialize_ConcurrentCallersShareExactlyThreeInitializedHandles()
    {
        var backend = new IdealizedMembershipBackend();
        var factoryEntered = Gate();
        var releaseFactory = Gate();
        var tables = new System.Collections.Concurrent.ConcurrentQueue<IdealizedMembershipTable>();
        var fixture = new MembershipTableTestFixture("concurrent-initialization", async (_, cluster, ct) =>
        {
            var table = backend.Create(cluster);
            tables.Enqueue(table);
            factoryEntered.TrySetResult();
            await releaseFactory.Task.WaitAsync(ct);
            return new MembershipTableTestHandle(table, () => backend.DisposeHandleAsync(cluster));
        }, backend.IsDeletedAsync);
        var ct = TestContext.Current.CancellationToken;
        var first = fixture.InitializeAsync(ct).AsTask();
        await factoryEntered.Task.WaitAsync(ct);
        var second = fixture.InitializeAsync(ct).AsTask();
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Single(tables);
        }
        finally
        {
            releaseFactory.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(ct);
            await fixture.DisposeAsync();
        }

        Assert.Equal(new[] { fixture.First, fixture.Second, fixture.OtherCluster }, tables.ToArray());
        Assert.All(tables, table => Assert.Equal(1, table.InitializeCalls));
        Assert.Equal(3, backend.CreatedHandles);
        Assert.Equal(3, backend.DisposedHandles);
        Assert.Equal(2, backend.Deletes);
    }

    [Fact]
    public async Task Initialize_CancelledQueuedCallerLeavesActiveInitializationIntact()
    {
        var backend = new IdealizedMembershipBackend();
        var factoryEntered = Gate();
        var releaseFactory = Gate();
        var fixture = new MembershipTableTestFixture("cancelled-initialization-waiter", async (_, cluster, ct) =>
        {
            var table = backend.Create(cluster);
            factoryEntered.TrySetResult();
            await releaseFactory.Task.WaitAsync(ct);
            return new MembershipTableTestHandle(table, () => backend.DisposeHandleAsync(cluster));
        }, backend.IsDeletedAsync);
        var ct = TestContext.Current.CancellationToken;
        var first = fixture.InitializeAsync(ct).AsTask();
        await factoryEntered.Task.WaitAsync(ct);
        using var cancelled = new CancellationTokenSource();
        var second = fixture.InitializeAsync(cancelled.Token).AsTask();
        try
        {
            cancelled.Cancel();
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(ct));
            Assert.Equal(cancelled.Token, failure.CancellationToken);
            Assert.False(first.IsCompleted);
            Assert.Equal(1, backend.CreatedHandles);
            Assert.Equal(0, backend.DisposedHandles);
            Assert.Equal(0, backend.Deletes);
        }
        finally
        {
            releaseFactory.TrySetResult();
            await first.WaitAsync(ct);
            await fixture.DisposeAsync();
        }

        Assert.Equal(1, Assert.IsType<IdealizedMembershipTable>(fixture.First).InitializeCalls);
        Assert.Equal(1, Assert.IsType<IdealizedMembershipTable>(fixture.Second).InitializeCalls);
        Assert.Equal(1, Assert.IsType<IdealizedMembershipTable>(fixture.OtherCluster).InitializeCalls);
        Assert.Equal(3, backend.CreatedHandles);
        Assert.Equal(3, backend.DisposedHandles);
        Assert.Equal(2, backend.Deletes);
    }

    [Fact]
    public async Task Initialize_SingletonFactory_IsRejectedAndAcquiredOwnerDisposedOnce()
    {
        var backend = new IdealizedMembershipBackend();
        var table = backend.Create("unused");
        var disposed = 0;
        var sharedHandle = new MembershipTableTestHandle(table, () => { disposed++; return ValueTask.CompletedTask; });
        var fixture = new MembershipTableTestFixture("singleton", (_, _) => ValueTask.FromResult(sharedHandle), backend.IsDeletedAsync);
        var failure = await Assert.ThrowsAsync<ClusteringConformanceException>(() => fixture.InitializeAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("same provider instance", failure.Message);
        Assert.Equal(1, disposed);
        await fixture.DisposeAsync();
        Assert.Equal(1, disposed);
    }

    [Fact]
    public async Task Initialize_PartialFactoryFailure_CleansAcquiredHandlesAndPreservesPrimary()
    {
        var backend = new IdealizedMembershipBackend();
        var expected = new InvalidOperationException("second construction failed");
        var created = 0;
        var disposed = 0;
        var fixture = new MembershipTableTestFixture("partial", (_, cluster, _) =>
        {
            if (++created == 2) throw expected;
            return ValueTask.FromResult(new MembershipTableTestHandle(backend.Create(cluster), () =>
            {
                disposed++;
                throw new InvalidOperationException("owner disposal failed");
            }));
        }, backend.IsDeletedAsync);
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InitializeAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Same(expected, actual);
        Assert.Equal(1, disposed);
        Assert.Equal(1, backend.Deletes);
        Assert.IsType<AggregateException>(actual.Data[ClusteringTestKitDiagnostics.CleanupFailureKey]);
    }

    [Fact]
    public async Task Run_CancelledPrimary_StillUsesIndependentTeardownAndDisposesAllOwners()
    {
        var backend = new IdealizedMembershipBackend();
        using var cancelled = new CancellationTokenSource();
        var primary = new OperationCanceledException(cancelled.Token);
        var fixture = backend.Fixture();
        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.RunAsync((_, _) =>
        {
            cancelled.Cancel();
            throw primary;
        }, cancelled.Token));
        Assert.Same(primary, actual);
        Assert.Equal(2, backend.Deletes);
        Assert.Equal(3, backend.DisposedHandles);
        await fixture.DisposeAsync();
        Assert.Equal(3, backend.DisposedHandles);
    }

    [Fact]
    public async Task Run_TeardownFailure_DoesNotReplaceAssertionAndDisposesRemainingOwners()
    {
        var backend = new IdealizedMembershipBackend();
        var disposed = 0;
        var fixture = new MembershipTableTestFixture("cleanup", (_, cluster, _) =>
            ValueTask.FromResult(new MembershipTableTestHandle(backend.Create(cluster), () =>
            {
                disposed++;
                if (disposed == 1) throw new InvalidOperationException("owner failure");
                return ValueTask.CompletedTask;
            })), backend.IsDeletedAsync);
        var primary = new ClusteringConformanceException("original assertion");
        var actual = await Assert.ThrowsAsync<ClusteringConformanceException>(() =>
            fixture.RunAsync((_, _) => throw primary, TestContext.Current.CancellationToken));
        Assert.Same(primary, actual);
        Assert.IsType<AggregateException>(actual.Data[ClusteringTestKitDiagnostics.CleanupFailureKey]);
        Assert.Equal(3, disposed);
        Assert.Equal(2, backend.Deletes);
    }

    [Fact]
    public async Task CreateAdditionalHandle_OwnedScopeHasExplicitInitializationAndLifetime()
    {
        var backend = new IdealizedMembershipBackend();
        var fixture = backend.Fixture();
        await fixture.RunAsync(async (f, ct) =>
        {
            var handle = await f.CreateAdditionalHandleAsync(f.ClusterId, ct);
            Assert.NotSame(f.First, handle.Table);
            await handle.Table.InitializeMembershipTableAsync(false, ct);
            var failure = await Assert.ThrowsAsync<ArgumentException>(() => f.CreateAdditionalHandleAsync("not-owned", ct).AsTask());
            Assert.Equal("clusterId", failure.ParamName);
            Assert.Equal(4, backend.CreatedHandles);
        }, TestContext.Current.CancellationToken);
        Assert.Equal(4, backend.DisposedHandles);
        Assert.Equal(2, backend.Deletes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateAdditionalHandle_DisposalWaitsForFactoryAndLateOwnerCleanup(bool cancelAcquisition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var backend = new IdealizedMembershipBackend { TerminalDeletion = true };
        var factoryEntered = Gate();
        var releaseFactory = Gate();
        var lateDisposalEntered = Gate();
        var releaseLateDisposal = Gate();
        var factoryCalls = 0;
        var disposalCalls = new int[4];
        var fixture = new MembershipTableTestFixture("gated-disposal", async (_, cluster, _) =>
        {
            var index = Interlocked.Increment(ref factoryCalls) - 1;
            var handle = new MembershipTableTestHandle(backend.Create(cluster), async () =>
            {
                Interlocked.Increment(ref disposalCalls[index]);
                if (index == 3)
                {
                    lateDisposalEntered.TrySetResult();
                    await releaseLateDisposal.Task.WaitAsync(ct);
                }
                await backend.DisposeHandleAsync(cluster);
            });
            if (index == 3)
            {
                factoryEntered.TrySetResult();
                await releaseFactory.Task.WaitAsync(ct);
            }
            return handle;
        }, backend.IsDeletedAsync);
        await fixture.InitializeAsync(ct);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var acquisition = fixture.CreateAdditionalHandleAsync(fixture.ClusterId, caller.Token).AsTask();
        await factoryEntered.Task.WaitAsync(ct);
        if (cancelAcquisition)
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);
        }
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.DisposeAsync(TimeSpan.Zero).AsTask());
        var disposal = fixture.DisposeAsync().AsTask();
        try
        {
            Assert.Equal(0, backend.Deletes);
            Assert.False(disposal.IsCompleted);
            releaseFactory.TrySetResult();
            await lateDisposalEntered.Task.WaitAsync(ct);
            Assert.False(disposal.IsCompleted);
            Assert.Equal(0, backend.Deletes);
            Assert.Equal(0, backend.DisposedHandles);
            Assert.Equal(1, Volatile.Read(ref disposalCalls[3]));
            releaseLateDisposal.TrySetResult();
            if (!cancelAcquisition)
            {
                var failure = await Assert.ThrowsAsync<ObjectDisposedException>(() => acquisition.WaitAsync(ct));
                Assert.Equal(nameof(MembershipTableTestFixture), failure.ObjectName);
            }
        }
        finally
        {
            releaseFactory.TrySetResult();
            releaseLateDisposal.TrySetResult();
            await disposal.WaitAsync(ct);
        }

        await fixture.DisposeAsync();
        Assert.Equal(4, backend.CreatedHandles);
        Assert.Equal(4, backend.DisposedHandles);
        Assert.Equal(new[] { 1, 1, 1, 1 }, disposalCalls);
        Assert.Equal(0, backend.OperationsAfterDeletion);
        Assert.Empty(backend.Partitions);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.CreateAdditionalHandleAsync(fixture.ClusterId, ct).AsTask());
        Assert.Equal(4, factoryCalls);
    }

    [Fact]
    public async Task CreateAdditionalHandle_RegistersBeforeDisposal_IsIncludedInOwnershipSnapshot()
    {
        var backend = new IdealizedMembershipBackend { TerminalDeletion = true };
        var factoryEntered = Gate();
        var releaseFactory = Gate();
        var calls = 0;
        var fixture = new MembershipTableTestFixture("registered-first", async (_, cluster, ct) =>
        {
            var handle = new MembershipTableTestHandle(backend.Create(cluster), () => backend.DisposeHandleAsync(cluster));
            if (Interlocked.Increment(ref calls) == 4)
            {
                factoryEntered.TrySetResult();
                await releaseFactory.Task.WaitAsync(ct);
            }
            return handle;
        }, backend.IsDeletedAsync);
        var ct = TestContext.Current.CancellationToken;
        await fixture.InitializeAsync(ct);
        var acquisition = fixture.CreateAdditionalHandleAsync(fixture.ClusterId, ct).AsTask();
        await factoryEntered.Task.WaitAsync(ct);
        releaseFactory.TrySetResult();
        var handle = await acquisition.WaitAsync(ct);

        await fixture.DisposeAsync();
        await handle.DisposeAsync();
        Assert.Equal(4, backend.CreatedHandles);
        Assert.Equal(4, backend.DisposedHandles);
        Assert.Equal(2, backend.Deletes);
        Assert.Equal(0, backend.OperationsAfterDeletion);
        Assert.Empty(backend.Partitions);
    }

    [Fact]
    public async Task CreateAdditionalHandle_HistoryRetiresWhileFactoryIsPending_DisposesOwnerAndRejectsRegistration()
    {
        var backend = new IdealizedMembershipBackend { TerminalDeletion = true };
        var factoryEntered = Gate();
        var releaseFactory = Gate();
        var calls = 0;
        var fixture = new MembershipTableTestFixture("retired-history", async (_, cluster, ct) =>
        {
            var handle = new MembershipTableTestHandle(backend.Create(cluster), () => backend.DisposeHandleAsync(cluster));
            if (Interlocked.Increment(ref calls) == 4)
            {
                factoryEntered.TrySetResult();
                await releaseFactory.Task.WaitAsync(ct);
            }
            return handle;
        }, backend.IsDeletedAsync);
        await fixture.RunAsync(async (f, ct) =>
        {
            var acquisition = f.CreateAdditionalHandleAsync(f.ClusterId, ct).AsTask();
            try
            {
                await factoryEntered.Task.WaitAsync(ct);
                await new MembershipTableTestRunner(f).DeleteMembershipTableEntries_DeletesOwnClusterAndPreservesOtherCluster(ct);
                Assert.Equal(0, backend.DisposedHandles);
            }
            finally
            {
                releaseFactory.TrySetResult();
            }
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => acquisition.WaitAsync(ct));
            Assert.Contains("cluster history ended", failure.Message);
            Assert.Equal(1, backend.DisposedHandles);
        }, TestContext.Current.CancellationToken);
        Assert.Equal(4, backend.DisposedHandles);
        Assert.Equal(2, backend.Deletes);
        Assert.Equal(0, backend.OperationsAfterDeletion);
        Assert.Empty(backend.Partitions);
    }

    [Fact]
    public async Task CreateAdditionalHandle_LateOwnerDisposalFailure_Propagates()
    {
        var backend = new IdealizedMembershipBackend();
        var factoryEntered = Gate();
        var releaseFactory = Gate();
        var expected = new InvalidOperationException("late-owner-disposal");
        var disposalCalls = 0;
        var fixture = new MembershipTableTestFixture("late-failure", async (_, cluster, ct) =>
        {
            var handle = new MembershipTableTestHandle(backend.Create(cluster), () =>
            {
                Interlocked.Increment(ref disposalCalls);
                throw expected;
            });
            factoryEntered.TrySetResult();
            await releaseFactory.Task.WaitAsync(ct);
            return handle;
        }, backend.IsDeletedAsync);
        var ct = TestContext.Current.CancellationToken;
        var acquisition = fixture.CreateAdditionalHandleAsync(fixture.ClusterId, ct).AsTask();
        await factoryEntered.Task.WaitAsync(ct);
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.DisposeAsync(TimeSpan.Zero).AsTask());
        releaseFactory.TrySetResult();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => acquisition.WaitAsync(ct));
        Assert.Same(expected, failure);
        Assert.Equal(1, disposalCalls);
        Assert.Equal(0, backend.Deletes);
        var cleanup = await Assert.ThrowsAsync<AggregateException>(() => fixture.DisposeAsync().AsTask());
        Assert.Same(expected, Assert.Single(cleanup.InnerExceptions));
        Assert.Equal(1, disposalCalls);
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Handle_DisposeAsync_OwnsOnlyExplicitDisposerAndRunsOnce()
    {
        var table = new IdealizedMembershipBackend().Create("A");
        var calls = 0;
        var handle = new MembershipTableTestHandle(table, () => { calls++; return ValueTask.CompletedTask; });
        Assert.Same(table, handle.Table);
        await handle.DisposeAsync();
        await handle.DisposeAsync();
        Assert.Equal(1, calls);
        Assert.Empty((await table.ReadAllAsync(TestContext.Current.CancellationToken)).Members);
    }

    [Fact]
    public async Task PublicArguments_AreValidatedBeforeConstructingProviders()
    {
        var backend = new IdealizedMembershipBackend();
        Assert.Throws<ArgumentNullException>(() => new MembershipTableTestHandle(null!));
        Assert.Throws<ArgumentNullException>(() => new MembershipTableTestFixture("provider", (Func<string, IMembershipTable>)null!, backend.IsDeletedAsync));
        Assert.Throws<ArgumentException>(() => new MembershipTableTestFixture(" ", backend.Create, backend.IsDeletedAsync));
        Assert.Equal("isDeletedAsync", Assert.Throws<ArgumentNullException>(() =>
            new MembershipTableTestFixture("provider", backend.Create, null!)).ParamName);
        var fixture = backend.Fixture();
        Assert.Throws<ArgumentException>(() => new MembershipTableTestRunner(fixture));
        await fixture.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MembershipTableTestRunner(fixture, concurrencyRowCount: 2));
        Assert.Equal(3, backend.CreatedHandles);
        await fixture.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.InitializeAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(3, backend.DisposedHandles);
    }
}
