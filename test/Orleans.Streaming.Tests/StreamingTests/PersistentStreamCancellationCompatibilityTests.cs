using NSubstitute;
using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests;

public class PersistentStreamCancellationCompatibilityTests
{
    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public async Task CancellationOverloads_FallBackToLegacyImplementations()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var adapter = Substitute.For<IQueueAdapter>();
        IQueueAdapterFactory adapterFactory = new LegacyQueueAdapterFactory(adapter);
        Assert.Same(adapter, await adapterFactory.CreateAdapter(cancellation.Token));

        IQueueAdapterReceiver receiver = new LegacyQueueAdapterReceiver();
        Assert.Empty(await receiver.GetQueueMessagesAsync(10, cancellation.Token));
        await receiver.MessagesDeliveredAsync([], cancellation.Token);
        Assert.True(((LegacyQueueAdapterReceiver)receiver).MessagesDelivered);

        IStreamQueueCheckpointer<string> checkpointer = new LegacyStreamQueueCheckpointer();
        IStreamQueueCheckpointerFactory checkpointerFactory = new LegacyStreamQueueCheckpointerFactory(checkpointer);
        Assert.Same(checkpointer, await checkpointerFactory.Create("partition", cancellation.Token));
        Assert.Equal("10", await checkpointer.Load(cancellation.Token));

        checkpointer.Update("20", DateTime.UtcNow, cancellation.Token);
        Assert.Equal("20", ((LegacyStreamQueueCheckpointer)checkpointer).Checkpoint);
    }

    private sealed class LegacyQueueAdapterFactory(IQueueAdapter adapter) : IQueueAdapterFactory
    {
        public Task<IQueueAdapter> CreateAdapter() => Task.FromResult(adapter);

        public IQueueAdapterCache GetQueueAdapterCache() => null!;

        public IStreamQueueMapper GetStreamQueueMapper() => null!;

        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
            => Task.FromResult<IStreamFailureHandler>(null!);
    }

    private sealed class LegacyQueueAdapterReceiver : IQueueAdapterReceiver
    {
        public bool MessagesDelivered { get; private set; }

        public Task Initialize(TimeSpan timeout) => Task.CompletedTask;

        public Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
            => Task.FromResult<IList<IBatchContainer>>([]);

        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        {
            MessagesDelivered = true;
            return Task.CompletedTask;
        }

        public Task Shutdown(TimeSpan timeout) => Task.CompletedTask;
    }

    private sealed class LegacyStreamQueueCheckpointerFactory(
        IStreamQueueCheckpointer<string> checkpointer) : IStreamQueueCheckpointerFactory
    {
        public Task<IStreamQueueCheckpointer<string>> Create(string partition)
            => Task.FromResult(checkpointer);
    }

    private sealed class LegacyStreamQueueCheckpointer : IStreamQueueCheckpointer<string>
    {
        public string Checkpoint { get; private set; } = "10";

        public bool CheckpointExists => true;

        public Task<string> Load() => Task.FromResult(Checkpoint);

        public void Update(string offset, DateTime utcNow) => Checkpoint = offset;

        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
