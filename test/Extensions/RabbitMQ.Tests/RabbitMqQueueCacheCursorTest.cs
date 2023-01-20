using System.Collections.Concurrent;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streaming.RabbitMQ.Adapters;
using Orleans.Streaming.RabbitMQ.Adapters.Cache;
using Xunit;

namespace RabbitMQ.Tests;

public class RabbitMqQueueCacheCursorTest
{
    [Fact]
    public void WhenInitializing_WithNonExistingMessage_ShouldNotMoveCursor()
    {
        var (cursor, _) = InitializeNewCursor(sequenceToken: 20);

        Assert.Null(cursor.GetCurrent(out _));
        Assert.False(cursor.MoveNext());
    }

    [Fact]
    public void WhenRecordDeliveryFailed_IsCalled_ShouldSetDeliveryFailed()
    {
        var (cursor, _) = InitializeNewCursor();
        cursor.MoveNext();
        var currentMessage = cursor.GetCurrent(out _);
        cursor.RecordDeliveryFailure();

        Assert.True(((RabbitMqBatchContainer)currentMessage).DeliveryFailed);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(0)]
    public void WhenDeliveryFailed_ShouldRetrySameMessageAfterMoveNext(int initialToken)
    {
        var (cursor, _) = InitializeNewCursor(sequenceToken: initialToken);
        cursor.MoveNext();
        var failedMessage = cursor.GetCurrent(out _);
        cursor.RecordDeliveryFailure();
        //Moving from current wont happen since it failed
        cursor.MoveNext();
        
        cursor.Refresh(new EventSequenceTokenV2(0));

        //Trying to move to the next message after a refresh
        cursor.MoveNext();
        var currentMessage = cursor.GetCurrent(out _);

        Assert.Equal(failedMessage.SequenceToken.SequenceNumber, currentMessage.SequenceToken.SequenceNumber);
    }

    [Fact]
    public void WhenDeliveryFailed_ShouldNotMoveToNextMessage()
    {
        var (cursor, _) = InitializeNewCursor();
        cursor.MoveNext();
        var currentMessage = cursor.GetCurrent(out _);
        cursor.RecordDeliveryFailure();

        Assert.False(cursor.MoveNext());
    }

    [Fact]
    public void WhenInitializing_FromDequeuedMessageToken_ShouldReturnNextMessage()
    {
        var (cursor, _) = InitializeNewCursor(messagesSize: 10, sequenceToken: 2, 3);

        //First MoveNext to move from consumed but already dequeued message
        cursor.MoveNext();
        //Second MoveNext to move into the message the consumer should read
        cursor.MoveNext();
        var message = cursor.GetCurrent(exception: out _);
        Assert.Equal(3, message?.SequenceToken?.SequenceNumber);
    }
    
    [Theory]
    [InlineData(9)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(0)]
    public void WhenInitializing_ShouldMoveToOneMessageBeforeToken(int initialToken)
    {
        var (cursor, _) = InitializeNewCursor(messagesSize: 10, sequenceToken: initialToken);

        var message = cursor.GetCurrent(exception: out _);
        Assert.Equal(initialToken > 0 ? initialToken - 1 : initialToken, message?.SequenceToken?.SequenceNumber ?? 0);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(0)]
    [InlineData(5)]
    public void WhenRefreshing_ShouldSeturrentMessageToHandshakeToken(int initialToken)
    {
        //Create cursor with 3 messages in queue
        var (cursor, processingMessages) = InitializeNewCursor(messagesSize: 10, sequenceToken: initialToken);

        var streamId = StreamId.Create("TestName", Guid.NewGuid());
        var events = new List<object> { new { Message = "hello" } };

        //3 new messages just arrived
        var newMessages = Enumerable.Range(3, 3);
        foreach (var messageSequenceToken in newMessages)
        {
            var newBatch = new RabbitMqBatchContainer(streamId, events, new EventSequenceTokenV2(messageSequenceToken));
            newBatch.NextBatch =
                messageSequenceToken < initialToken ? new RabbitMqBatchContainer(streamId, events, new EventSequenceTokenV2(messageSequenceToken + 1)) : null;
            processingMessages.Enqueue(newBatch);
        }

        cursor.Refresh(new EventSequenceTokenV2(0));
        var currentMessage = cursor.GetCurrent(exception: out _);
        Assert.Equal(currentMessage.SequenceToken.SequenceNumber, initialToken);
    }

    [Fact]
    public void WhenMovingNext_ShouldSetCurrentMessageToTokenAfterLastMessage()
    {
        var (cursor, _) = InitializeNewCursor(messagesSize: 10, sequenceToken: 9);

        var lastMessage = cursor.GetCurrent(exception: out _);
        cursor.MoveNext();
        var currentMessage = cursor.GetCurrent(out _);
        Assert.True(lastMessage.SequenceToken.SequenceNumber < currentMessage.SequenceToken.SequenceNumber);
    }

    private (RabbitMqQueueCacheCursor, ConcurrentQueue<RabbitMqBatchContainer>) InitializeNewCursor(int messagesSize = 10, long sequenceToken = 0, int startMessagesFrom = 0)
    {
        var processingMessages = CreateProcessingMessages(messagesSize, startMessagesFrom);

        var cursor = CreateCacheCursor(processingMessages, new EventSequenceTokenV2(sequenceToken));
        return (cursor, processingMessages);
    }

    private static ConcurrentQueue<RabbitMqBatchContainer> CreateProcessingMessages(int messagesSize = 10, int startFrom = 0)
        => CreateProcessingMessages(StreamId.Create("TestName", Guid.NewGuid()), messagesSize, startFrom);

    private static ConcurrentQueue<RabbitMqBatchContainer> CreateProcessingMessages(StreamId streamId, int messagesSize = 10, int startFrom = 0)
    {
        var events = new List<object> { new { Message = "hello" } };

        var processingMessages = new ConcurrentQueue<RabbitMqBatchContainer>(Enumerable.Range(startFrom, messagesSize).Select(i =>
        {
            var newBatch = new RabbitMqBatchContainer(streamId, events, new EventSequenceTokenV2(i));
            newBatch.NextBatch =
                i < (messagesSize - 1) ? new RabbitMqBatchContainer(streamId, events, new EventSequenceTokenV2(i + 1)) : null;
            return newBatch;
        }));
        return processingMessages;
    }


    private RabbitMqQueueCacheCursor CreateCacheCursor(ConcurrentQueue<RabbitMqBatchContainer> processingMessages, EventSequenceTokenV2 handshakeToken)
        => new (handshakeToken, processingMessages, _ => { }, () => { }, () => processingMessages);
}