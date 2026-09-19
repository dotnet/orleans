using Orleans.Streams;
using TestExtensions;
using UnitTests.StreamingTests;

namespace ServiceBus.Tests.CheckpointerTests;

[TestSuite("BVT")]
[TestProvider("EventHub")]
[TestArea("Streaming")]
[TestCategory("EventHub"), TestCategory("Streaming")]
public sealed class AzureTableStreamQueueCheckpointerContractTests : StreamQueueCheckpointerTests
{
    protected override OffsetRegressionPolicy RegressionPolicy => OffsetRegressionPolicy.Ignore;

    protected override Task<IStreamQueueCheckpointer<string>> CreateCheckpointer(
        ControllableCheckpointStore store)
        => Task.FromResult<IStreamQueueCheckpointer<string>>(
            new AzureTableStreamQueueCheckpointer(
                new TestStore(store),
                PersistInterval,
                StreamCheckpointComparers.Numeric));

    private sealed class TestStore(ControllableCheckpointStore store) : IStreamCheckpointStore
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
            var persisted = await store.Write(checkpoint).ConfigureAwait(false);
            return new(persisted, persisted);
        }
    }
}
