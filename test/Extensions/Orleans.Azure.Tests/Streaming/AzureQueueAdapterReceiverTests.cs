using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.AzureUtils;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;

namespace Tester.AzureUtils.Streaming;

[TestCategory("Streaming")]
[TestSuite("BVT")]
[TestProvider("AzureStorage")]
[TestArea("Streaming")]
public class AzureQueueAdapterReceiverTests
{
    [Fact]
    public async Task ShutdownReleasesPendingMessagesForReplacementReceiver()
    {
        var message = CreateMessage();
        var queue = new TestQueueDataManager(message);
        var adapter = new TestQueueDataAdapter();

        var receiver = new AzureQueueAdapterReceiver("test-queue", NullLoggerFactory.Instance, queue, adapter);
        await receiver.Initialize(TimeSpan.FromSeconds(1));
        var received = Assert.Single(await receiver.GetQueueMessagesAsync(1));

        await receiver.Shutdown(TimeSpan.FromSeconds(1));

        Assert.Same(message, Assert.Single(queue.ReleasedMessages));

        var replacement = new AzureQueueAdapterReceiver("test-queue", NullLoggerFactory.Instance, queue, adapter);
        await replacement.Initialize(TimeSpan.FromSeconds(1));
        var redelivered = Assert.Single(await replacement.GetQueueMessagesAsync(1));

        Assert.Equal("payload", Assert.IsType<TestBatchContainer>(received).Payload);
        Assert.Equal("payload", Assert.IsType<TestBatchContainer>(redelivered).Payload);
    }

    [Fact]
    public async Task ShutdownReleasesMessageReceivedAfterShutdownBegins()
    {
        var message = CreateMessage();
        var queue = new DelayedQueueDataManager(message);
        var receiver = new AzureQueueAdapterReceiver(
            "test-queue",
            NullLoggerFactory.Instance,
            queue,
            new TestQueueDataAdapter());
        await receiver.Initialize(TimeSpan.FromSeconds(1));

        var receiveTask = receiver.GetQueueMessagesAsync(1);
        await queue.ReceiveStarted.Task;
        var shutdownTask = receiver.Shutdown(TimeSpan.FromSeconds(1));
        queue.CompleteReceive();

        await queue.ReleaseStarted.Task;
        Assert.False(shutdownTask.IsCompleted);
        queue.CompleteRelease();

        await Task.WhenAll(receiveTask, shutdownTask);
        Assert.Empty(await receiveTask);
        Assert.Same(message, Assert.Single(queue.ReleasedMessages));
    }

    [Fact]
    public async Task DeliveredMessagesAreDeletedInsteadOfReleased()
    {
        var message = CreateMessage();
        var queue = new TestQueueDataManager(message);
        var receiver = new AzureQueueAdapterReceiver(
            "test-queue",
            NullLoggerFactory.Instance,
            queue,
            new TestQueueDataAdapter());
        await receiver.Initialize(TimeSpan.FromSeconds(1));
        var received = Assert.Single(await receiver.GetQueueMessagesAsync(1));

        await receiver.MessagesDeliveredAsync([received]);
        await receiver.Shutdown(TimeSpan.FromSeconds(1));

        Assert.Same(message, Assert.Single(queue.DeletedMessages));
        Assert.Empty(queue.ReleasedMessages);
    }

    [Fact]
    public async Task DeleteFailureLeavesMessagePendingForShutdownRelease()
    {
        var message = CreateMessage();
        var queue = new TestQueueDataManager(message)
        {
            DeleteException = new InvalidOperationException("delete failed")
        };
        var receiver = new AzureQueueAdapterReceiver(
            "test-queue",
            NullLoggerFactory.Instance,
            queue,
            new TestQueueDataAdapter());
        await receiver.Initialize(TimeSpan.FromSeconds(1));
        var received = Assert.Single(await receiver.GetQueueMessagesAsync(1));

        await receiver.MessagesDeliveredAsync([received]);
        await receiver.Shutdown(TimeSpan.FromSeconds(1));

        Assert.Same(message, Assert.Single(queue.DeletedMessages));
        Assert.Same(message, Assert.Single(queue.ReleasedMessages));
    }

    private static QueueMessage CreateMessage()
    {
        var now = DateTimeOffset.UtcNow;
        return QueuesModelFactory.QueueMessage(
            "message-id",
            "pop-receipt",
            "payload",
            dequeueCount: 1,
            nextVisibleOn: now.AddMinutes(1),
            insertedOn: now,
            expiresOn: now.AddDays(1));
    }

    private sealed class TestQueueDataManager(QueueMessage message) : IAzureQueueDataManager
    {
        private readonly Queue<QueueMessage> _messages = new([message]);

        public List<QueueMessage> DeletedMessages { get; } = [];

        public List<QueueMessage> ReleasedMessages { get; } = [];

        public Exception? DeleteException { get; init; }

        public Task InitQueueAsync() => Task.CompletedTask;

        public Task<IEnumerable<QueueMessage>> GetQueueMessages(int? count = null)
        {
            if (_messages.TryDequeue(out var message))
            {
                return Task.FromResult<IEnumerable<QueueMessage>>([message]);
            }

            return Task.FromResult<IEnumerable<QueueMessage>>([]);
        }

        public Task DeleteQueueMessage(QueueMessage message)
        {
            DeletedMessages.Add(message);
            return DeleteException is null ? Task.CompletedTask : Task.FromException(DeleteException);
        }

        public Task ReleaseQueueMessage(QueueMessage message, CancellationToken cancellationToken)
        {
            ReleasedMessages.Add(message);
            _messages.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    private sealed class DelayedQueueDataManager(QueueMessage message) : IAzureQueueDataManager
    {
        private readonly TaskCompletionSource<IEnumerable<QueueMessage>> _receive =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReceiveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<QueueMessage> ReleasedMessages { get; } = [];

        public Task InitQueueAsync() => Task.CompletedTask;

        public Task<IEnumerable<QueueMessage>> GetQueueMessages(int? count = null)
        {
            ReceiveStarted.TrySetResult();
            return _receive.Task;
        }

        public Task DeleteQueueMessage(QueueMessage message) => Task.CompletedTask;

        public async Task ReleaseQueueMessage(QueueMessage message, CancellationToken cancellationToken)
        {
            ReleasedMessages.Add(message);
            ReleaseStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        public void CompleteReceive() => _receive.TrySetResult([message]);

        public void CompleteRelease() => _release.TrySetResult();
    }

    private sealed class TestQueueDataAdapter : IQueueDataAdapter<string, IBatchContainer>
    {
        public string ToQueueMessage<T>(
            StreamId streamId,
            IEnumerable<T> events,
            StreamSequenceToken? token,
            Dictionary<string, object>? requestContext) => throw new NotSupportedException();

        public IBatchContainer FromQueueMessage(string queueMessage, long sequenceId) =>
            new TestBatchContainer(queueMessage, sequenceId);
    }

    private sealed class TestBatchContainer(string payload, long sequenceId) : IBatchContainer
    {
        public string Payload { get; } = payload;

        public StreamId StreamId { get; } = StreamId.Create("test", Guid.Empty);

        public StreamSequenceToken SequenceToken { get; } = new EventSequenceTokenV2(sequenceId);

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() => [];

        public bool ImportRequestContext() => false;
    }
}
