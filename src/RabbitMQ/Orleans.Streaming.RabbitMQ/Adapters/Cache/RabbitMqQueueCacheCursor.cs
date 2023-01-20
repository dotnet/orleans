using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Orleans.Streams;

namespace Orleans.Streaming.RabbitMQ.Adapters.Cache;

internal class RabbitMqQueueCacheCursor : IQueueCacheCursor
{
    private readonly StreamSequenceToken _handshakeToken;
    private ConcurrentQueue<RabbitMqBatchContainer> _processingMessages;
    private readonly Action<RabbitMqBatchContainer> _onMessageRead;
    private readonly Func<ConcurrentQueue<RabbitMqBatchContainer>> _onPreRefresh;
    private readonly Action _onDispose;
    private readonly Action _onRetryMessage;
    private RabbitMqBatchContainer _current;

    private bool _movedFromDeliveredMessage;

    public RabbitMqQueueCacheCursor(StreamSequenceToken handshakeToken,
        ConcurrentQueue<RabbitMqBatchContainer> processingMessages,
        Action<RabbitMqBatchContainer> onMessageRead,
        Action onRetryMessage,
        Func<ConcurrentQueue<RabbitMqBatchContainer>> onPreRefresh, Action onDispose)
    {
        _handshakeToken = handshakeToken;
        _onMessageRead = onMessageRead;
        _onPreRefresh = onPreRefresh;
        _onDispose = onDispose;
        _onRetryMessage = onRetryMessage;
        Initialize(processingMessages);
    }

    /// <summary>
    /// When initializing, the current HandshakeToken is taking into consideration, moving the cursor to until 1 batch before the specified HandshakeToken.
    /// The reason for this is because the PullingAgent will be moving the cursor only twice after making a handshake with the consumer,
    /// so we need to initialize the cursor to make it ready for the PullingAgent.
    /// <br/>
    /// Example:
    ///     if the provided HandshakeToken during instantiation is 4 and there are 8 messages ([0, 1, 2, 3, 4, 5, 6, 7]) to process,
    ///     when calling GetCurrent(out _) for the first time, the batch containing a HandshakeToken of sequence 3 will be returned
    /// </summary>
    /// <param name="processingMessages">All the messages that we received from the QueueCache for this particular consumer cursor</param>
    private void Initialize(ConcurrentQueue<RabbitMqBatchContainer> processingMessages)
    {
        _processingMessages = processingMessages;

        if (_handshakeToken.SequenceNumber == 0)
        {
            return;
        }

        RabbitMqBatchContainer nextBatch;

        nextBatch = GetNextBatch();

        //Check if next item in the stream messages is the next item the consumer wants to read
        //If it is, the cursor will be moved twice by the pulling agent(first moving to the last consumed message,
        //then moving to the new message and delivering it to the consumer), so we don't have to internally move it anymore
        if (nextBatch?.SequenceToken.SequenceNumber >= _handshakeToken.SequenceNumber)
        {
            return;
        }

        nextBatch = MoveToMessageBeforeHandshakeToken();

        //If at this point we couldn't find the last consumed message,
        //it means that the pulling agent is still reading messages and this message we are looking for is not in the cache yet,
        //so we CAN NOT trust this cursor to be read by the consumer, as more messages might come in that the consumer has already read
        if (nextBatch is null || nextBatch.SequenceToken.SequenceNumber < _handshakeToken.SequenceNumber)
        {
            if (_current is not null)
            {
                PurgeCurrentMessage();
            }

            //The MoveNext method will return false until the cursor is refreshed
            _processingMessages = new();
        }
    }

    private void PurgeCurrentMessage()
    {
        //Only messages that won't be retried (aka, didn't fail to process on the last run) should be purged,
        //otherwise we would be losing failed messages
        if (_current is { WillRetry: true })
        {
            _current = null;
        }
        else
        {
            _processingMessages.TryDequeue(out _);
        }

        _onMessageRead(_current);
        _current = null;
    }

    private RabbitMqBatchContainer MoveToMessageBeforeHandshakeToken()
    {
        bool hasCurrent;
        do
        {
            hasCurrent = MoveNext();
        } while (hasCurrent && _current?.NextBatch?.SequenceToken.SequenceNumber < _handshakeToken.SequenceNumber);

        return GetNextBatch();
    }

    private RabbitMqBatchContainer GetNextBatch()
    {
        if (_current is null)
        {
            _processingMessages.TryPeek(out var nextQueueItem);
            return nextQueueItem;
        }

        return _current?.NextBatch;
    }
    
    public void Dispose() => _onDispose();

    public IBatchContainer GetCurrent(out Exception exception)
    {
        exception = null;
        return _current;
    }

    public bool MoveNext()
    {
        if (_current is { DeliveryFailed: true })
        {
            //Reset failed state so we can retry process this message when the cursor hit this point again
            _current.DeliveryFailed = false;
            _current.WillRetry = true;
            PurgeCurrentMessage();
            return false;
        }

        RabbitMqBatchContainer message;

        if (_current is null)
        {
            //Sometimes the PullingAgent expects a StartToken to be sent to the consumer, instead, a DeliveryToken is sent.
            //The consumer will reject the message and reinitialize the cursor.
            //This condition should only be true for the first time the cursor is requested to MoveNext, avoiding losing the current message
            if (_processingMessages.TryPeek(out message) &&
                message.SequenceToken.SequenceNumber > _handshakeToken.SequenceNumber && LastRefreshToken is null &&
                !_movedFromDeliveredMessage)
            {
                _movedFromDeliveredMessage = true;
                return false;
            }

            _processingMessages.TryPeek(out message);

            _current = message;

            if (_current is { WillRetry: true })
            {
                //Reset retry flag so that the cursor can try deliver this message to the consumer once again
                _current.WillRetry = false;
                _current = null;

                //Let the queue cache know that this message is now being retried/processed again
                _onRetryMessage();
            }

            return _current is not null;
        }

        PurgeCurrentMessage();

        _processingMessages.TryPeek(out message);

        _current = message;

        return _current is not null;
    }

    /// <summary>
    /// Refreshing the cursor will move it to the batch containing a greater or equal Token to the the provided HandshakeToken on instantiation.
    /// <br/>
    /// Example:
    ///     if the provided HandshakeToken during instantiation is 4 and there are 8 messages to process ([0, 1, 2, 3, 4, 5, 6, 7]),
    ///     when calling GetCurrent(out _) for the first time after this refresh, the batch containing a HandshakeToken of sequence 4 will be returned
    /// </summary>
    /// <param name="token"></param>
    public void Refresh(StreamSequenceToken token)
    {
        //Now that we have access again to the current processingMessages, we can just start reading messages again
        LastRefreshToken = token;
        var processingMessages = _onPreRefresh?.Invoke();

        //Create new cursor that only goes to the message before the handshakeToken
        var newCursor =
            new RabbitMqQueueCacheCursor(_handshakeToken, processingMessages, _onMessageRead, _onRetryMessage, null, null);
        var nextBatch = newCursor.GetNextBatch();

        //Since the initialization came from a refresh, it means that unlike a first initialization,
        //the pulling agent will be moving this cursor only once, that is, right before getting the current message, so we need to move it from the last processed message here.
        if (nextBatch?.SequenceToken.SequenceNumber >= _handshakeToken.SequenceNumber)
        {
            if (nextBatch.SequenceToken.SequenceNumber == _handshakeToken.SequenceNumber)
            {
                newCursor.MoveNext();
                _current = newCursor._current;
            }

            _processingMessages = processingMessages;
        }
    }

    public StreamSequenceToken LastRefreshToken { get; private set; }

    public void RecordDeliveryFailure()
    {
        if (_current is not null)
        {
            _current.DeliveryFailed = true;
        }
    }
}