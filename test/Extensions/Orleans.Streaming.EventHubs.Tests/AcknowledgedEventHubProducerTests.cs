using Azure.Messaging.EventHubs;
using Orleans.Streaming.EventHubs;
using Xunit;

namespace ServiceBus.Tests;

[TestSuite("BVT")]
[TestProvider("EventHub")]
[TestArea("Streaming")]
[TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("BVT")]
public sealed class AcknowledgedEventHubProducerTests
{
    [Fact]
    public async Task SendAsync_WaitsForAcknowledgement()
    {
        var client = new TestBufferedClient();
        var producer = new AcknowledgedEventHubProducer(client);
        var eventData = new EventData(BinaryData.FromString("one"));

        var send = producer.SendAsync(eventData, "key");
        await client.EventEnqueued;

        Assert.False(send.IsCompleted);
        client.Succeed(eventData);
        await send;
    }

    [Fact]
    public async Task SendAsync_PropagatesBatchFailure()
    {
        var client = new TestBufferedClient();
        var producer = new AcknowledgedEventHubProducer(client);
        var eventData = new EventData(BinaryData.FromString("one"));
        var expected = new InvalidOperationException("send failed");

        var send = producer.SendAsync(eventData, "key");
        await client.EventEnqueued;
        client.Fail(expected, eventData);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => send);
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task SendAsync_WaitsForBufferCapacityAndAcknowledgement()
    {
        var enqueueCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestBufferedClient { EnqueueCompletion = enqueueCompletion.Task };
        var producer = new AcknowledgedEventHubProducer(client);
        var eventData = new EventData(BinaryData.FromString("one"));

        var send = producer.SendAsync(eventData, "key");
        await client.EventEnqueued;

        Assert.False(send.IsCompleted);
        enqueueCompletion.SetResult();
        await Task.Yield();
        Assert.False(send.IsCompleted);

        client.Succeed(eventData);
        await send;
    }

    [Fact]
    public async Task CloseAsync_WaitsForEnqueueThenFlushesPendingEvents()
    {
        var enqueueCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestBufferedClient { EnqueueCompletion = enqueueCompletion.Task };
        var producer = new AcknowledgedEventHubProducer(client);
        var eventData = new EventData(BinaryData.FromString("one"));
        var send = producer.SendAsync(eventData, "key");
        await client.EventEnqueued;

        var close = producer.CloseAsync(CancellationToken.None);
        Assert.False(client.CloseCalled.IsCompleted);

        enqueueCompletion.SetResult();
        await client.CloseCalled;
        Assert.False(close.IsCompleted);

        client.Succeed(eventData);
        client.CloseCompletion.SetResult();
        await Task.WhenAll(send, close);
    }

    [Fact]
    public async Task CloseAsync_FailsPublicationsMissingAResult()
    {
        var client = new TestBufferedClient();
        var producer = new AcknowledgedEventHubProducer(client);
        var eventData = new EventData(BinaryData.FromString("one"));
        var send = producer.SendAsync(eventData, "key");
        await client.EventEnqueued;

        client.CloseCompletion.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => producer.CloseAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => send);
    }

    [Fact]
    public async Task SendAsync_AfterCloseIsRejected()
    {
        var client = new TestBufferedClient();
        var producer = new AcknowledgedEventHubProducer(client);
        client.CloseCompletion.SetResult();
        await producer.CloseAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => producer.SendAsync(new EventData(BinaryData.FromString("one")), "key"));
    }

    private sealed class TestBufferedClient : IBufferedEventHubClient
    {
        private readonly TaskCompletionSource _eventEnqueued =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closeCalled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<IReadOnlyList<EventData>>? BatchSucceeded;

        public event Action<IReadOnlyList<EventData>, Exception>? BatchFailed;

        public Task EnqueueCompletion { get; set; } = Task.CompletedTask;

        public Task EventEnqueued => _eventEnqueued.Task;

        public Task CloseCalled => _closeCalled.Task;

        public TaskCompletionSource CloseCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnqueueEventAsync(EventData eventData, string partitionKey)
        {
            _eventEnqueued.TrySetResult();
            return EnqueueCompletion;
        }

        public Task<string[]> GetPartitionIdsAsync() => Task.FromResult(new[] { "0", "1" });

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            _closeCalled.TrySetResult();
            return CloseCompletion.Task.WaitAsync(cancellationToken);
        }

        public void Succeed(params EventData[] eventBatch) => BatchSucceeded?.Invoke(eventBatch);

        public void Fail(Exception exception, params EventData[] eventBatch) =>
            BatchFailed?.Invoke(eventBatch, exception);
    }
}
