using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class ReusableStreamQueueCheckpointerTests : StreamQueueCheckpointerTests
{
    protected override OffsetRegressionPolicy RegressionPolicy => OffsetRegressionPolicy.Ignore;

    protected override Task<IStreamQueueCheckpointer<string>> CreateCheckpointer(
        ControllableCheckpointStore store)
        => Task.FromResult<IStreamQueueCheckpointer<string>>(
            new StreamQueueCheckpointer(
                new TestCheckpointStore(store),
                new StreamQueueCheckpointerOptions
                {
                    CheckpointComparer = StreamCheckpointComparers.Numeric,
                    PersistInterval = PersistInterval,
                }));

    [Fact]
    public async Task ConditionalConflict_RetriesWithReturnedVersion()
    {
        var store = new ConflictingCheckpointStore();
        var checkpointer = new StreamQueueCheckpointer(
            store,
            new StreamQueueCheckpointerOptions
            {
                CheckpointComparer = StreamCheckpointComparers.Numeric,
            });
        Assert.Equal("10", await checkpointer.Load(CancellationToken.None));

        checkpointer.Update("30", DateTime.UtcNow, CancellationToken.None);
        await checkpointer.FlushAsync(CancellationToken.None);

        Assert.Equal(["version-1", "version-2"], store.ExpectedVersions);
        Assert.Equal("30", (await store.Load(CancellationToken.None)).Checkpoint);
    }

    [Fact]
    public async Task ConditionalConflict_WithEmptyPersistedCheckpoint_RetriesFirstCheckpoint()
    {
        var store = new EmptyCheckpointConflictStore();
        var checkpointer = new StreamQueueCheckpointer(
            store,
            new StreamQueueCheckpointerOptions
            {
                CheckpointComparer = StreamCheckpointComparers.Numeric,
            });
        Assert.Equal(string.Empty, await checkpointer.Load(CancellationToken.None));

        checkpointer.Update("10", DateTime.UtcNow, CancellationToken.None);
        await checkpointer.FlushAsync(CancellationToken.None);

        Assert.Equal(["version-1", "version-2"], store.ExpectedVersions);
        Assert.Equal("10", (await store.Load(CancellationToken.None)).Checkpoint);
    }

    [Fact]
    public async Task SamePendingCheckpoint_RetriesAfterFailedWriteAtPersistInterval()
    {
        var store = new ControllableCheckpointStore("10");
        var checkpointer = await CreateCheckpointer(store);
        Assert.Equal("10", await checkpointer.Load(CancellationToken.None));
        store.FailNextWrite(new InvalidOperationException("checkpoint write failed"));

        checkpointer.Update("20", DateTime.UtcNow, CancellationToken.None);
        await store.WaitForWriteAttempts(1);
        checkpointer.Update("20", DateTime.UtcNow + PersistInterval, CancellationToken.None);
        await store.WaitForCompletedWrites(1);

        Assert.Equal(["20", "20"], store.WriteAttempts);
        Assert.Equal(["20"], store.CompletedWrites);
        Assert.Equal("20", store.PersistedCheckpoint);
    }

    [Fact]
    public async Task ConditionalConflict_WithoutComparerAdoptsAuthoritativeCheckpoint()
    {
        var store = new BlockingAuthoritativeConflictStore();
        var checkpointer = new StreamQueueCheckpointer(
            store,
            new StreamQueueCheckpointerOptions { CheckpointComparer = null });
        Assert.Equal("10", await checkpointer.Load(CancellationToken.None));

        checkpointer.Update("20", DateTime.UtcNow, CancellationToken.None);
        await store.FirstUpdateStarted.Task;
        checkpointer.Update("30", DateTime.UtcNow, CancellationToken.None);
        store.ReleaseFirstUpdate.SetResult();
        await checkpointer.FlushAsync(CancellationToken.None);

        Assert.Equal(["20"], store.Attempts);
        Assert.Equal("40", (await store.Load(CancellationToken.None)).Checkpoint);
    }

    private sealed class TestCheckpointStore(ControllableCheckpointStore store) : IStreamCheckpointStore
    {
        public async ValueTask<StreamCheckpointStoreState> Load(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var checkpoint = await store.Load().ConfigureAwait(false);
            return new(checkpoint, checkpoint);
        }

        public async ValueTask<StreamCheckpointStoreState> Update(
            string checkpoint,
            string expectedVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var persistedCheckpoint = await store.Write(checkpoint).ConfigureAwait(false);
            return new(persistedCheckpoint, persistedCheckpoint);
        }
    }

    private sealed class ConflictingCheckpointStore : IStreamCheckpointStore
    {
        private StreamCheckpointStoreState state = new("10", "version-1");
        private bool conflict = true;

        public List<string> ExpectedVersions { get; } = [];

        public ValueTask<StreamCheckpointStoreState> Load(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(state);
        }

        public ValueTask<StreamCheckpointStoreState> Update(
            string checkpoint,
            string expectedVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExpectedVersions.Add(expectedVersion);
            if (conflict)
            {
                conflict = false;
                state = new("20", "version-2");
            }
            else
            {
                Assert.Equal(state.Version, expectedVersion);
                state = new(checkpoint, "version-3");
            }

            return ValueTask.FromResult(state);
        }
    }

    private sealed class EmptyCheckpointConflictStore : IStreamCheckpointStore
    {
        private StreamCheckpointStoreState state = new(string.Empty, "version-1");
        private bool conflict = true;

        public List<string> ExpectedVersions { get; } = [];

        public ValueTask<StreamCheckpointStoreState> Load(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(state);
        }

        public ValueTask<StreamCheckpointStoreState> Update(
            string checkpoint,
            string expectedVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExpectedVersions.Add(expectedVersion);
            if (conflict)
            {
                conflict = false;
                state = new(string.Empty, "version-2");
            }
            else
            {
                Assert.Equal(state.Version, expectedVersion);
                state = new(checkpoint, "version-3");
            }

            return ValueTask.FromResult(state);
        }
    }

    private sealed class BlockingAuthoritativeConflictStore : IStreamCheckpointStore
    {
        private StreamCheckpointStoreState state = new("10", "version-1");

        public TaskCompletionSource FirstUpdateStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstUpdate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Attempts { get; } = [];

        public ValueTask<StreamCheckpointStoreState> Load(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(state);
        }

        public async ValueTask<StreamCheckpointStoreState> Update(
            string checkpoint,
            string expectedVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts.Add(checkpoint);
            FirstUpdateStarted.TrySetResult();
            await ReleaseFirstUpdate.Task.WaitAsync(cancellationToken);
            state = new("40", "version-2");
            return state;
        }
    }
}
