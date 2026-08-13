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
}
