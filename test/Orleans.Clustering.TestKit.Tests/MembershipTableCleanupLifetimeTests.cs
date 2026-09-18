using Orleans.Runtime;
using Xunit;

namespace Orleans.Clustering.TestKit.Tests;

public sealed class MembershipTableCleanupLifetimeTests
{
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

    // Exercises the interface's tokenless compatibility dispatch, which cancels waits separately from operations.
    private sealed class LegacyTable(IdealizedMembershipTable inner, Func<Task> beforeDelete) : IMembershipTable
    {
        public Task InitializeMembershipTable(bool tryInitTableVersion) => inner.InitializeMembershipTableAsync(tryInitTableVersion);
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
