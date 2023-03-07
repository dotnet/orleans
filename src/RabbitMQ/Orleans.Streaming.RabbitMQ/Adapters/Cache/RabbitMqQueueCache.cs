using System.Collections.Concurrent;
using Orleans.Runtime;
using Orleans.Streaming.RabbitMQ.RabbitMQ;
using Orleans.Streams;

namespace Orleans.Streaming.RabbitMQ.Adapters.Cache;

internal class CursorsCounter
{
    private int _count = 0;

    public CursorsCounter()
    {
        
    }

    private CursorsCounter(int initialCount)
    {
        _count = initialCount;
    }

    public int Count => _count;

    public int Increment()
        => Interlocked.Increment(ref _count);

    public int Decrement()
        => Interlocked.Decrement(ref _count);

    public static implicit operator CursorsCounter(int initialCount) => new (initialCount);
}

internal class RabbitMqQueueCache : IQueueCache
{
    private int _countProcessingMessages;
    private readonly List<RabbitMqBatchContainer> _messagesToPurge = new();

    private readonly ConcurrentDictionary<StreamId, Lazy<ConcurrentQueue<RabbitMqBatchContainer>>>
        _processingMessages = new();

    private readonly RabbitMqQueueCacheOptions _cacheOptions;
    private readonly ConcurrentDictionary<StreamId, CursorsCounter> _activeStreamCursorsCount = new();
    private readonly ConcurrentDictionary<StreamId, CursorsCounter> _activeStreamCursorsProcessedMessageCount = new ();

    public RabbitMqQueueCache(RabbitMqQueueCacheOptions cacheOptions)
    {
        _cacheOptions = cacheOptions;
    }


    public int GetMaxAddCount() => _cacheOptions.CacheSize;

    public void AddToCache(IList<IBatchContainer> messages)
    {
        Interlocked.Add(ref _countProcessingMessages, messages.Count);
        foreach (var batchContainers in messages.GroupBy(m => m.StreamId))
        {
            _processingMessages.AddOrUpdate(batchContainers.Key,
                _ => new Lazy<ConcurrentQueue<RabbitMqBatchContainer>>(
                    () => EnqueueBatchContainers(batchContainers, new ConcurrentQueue<RabbitMqBatchContainer>()),
                    LazyThreadSafetyMode.ExecutionAndPublication),
                (_, lazyQueue) => new Lazy<ConcurrentQueue<RabbitMqBatchContainer>>(
                    () => EnqueueBatchContainers(batchContainers, lazyQueue.Value),
                    LazyThreadSafetyMode.ExecutionAndPublication));
        }
    }

    private ConcurrentQueue<RabbitMqBatchContainer> EnqueueBatchContainers(IGrouping<StreamId, IBatchContainer> batchContainers, ConcurrentQueue<RabbitMqBatchContainer> queue)
    {
        var linkedList =
            new LinkedList<RabbitMqBatchContainer>(batchContainers.Cast<RabbitMqBatchContainer>());
        var currentItem = linkedList.First;

        while (currentItem is not null)
        {
            currentItem.Value.NextBatch = currentItem.Next?.Value;
            queue.Enqueue(currentItem.Value);
            currentItem = currentItem.Next;
        }

        return queue;
    }

    public bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
    {
        if (_messagesToPurge.Count < 1)
        {
            purgedItems = null;
            return false;
        }

        purgedItems = _messagesToPurge.Cast<IBatchContainer>().ToList();
        _messagesToPurge.Clear();

        return purgedItems.Count > 0;
    }

    public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken token)
    {
        if (_processingMessages.TryGetValue(streamId, out var lazyQueue))
        {
            var cursor = new RabbitMqQueueCacheCursor(token,
                new ConcurrentQueue<RabbitMqBatchContainer>(lazyQueue.Value), messageRead =>
                {
                    var cursorProcessedMessageCount =
                        _activeStreamCursorsProcessedMessageCount.GetOrAdd(streamId, new CursorsCounter());
                    if (_activeStreamCursorsCount.TryGetValue(streamId, out var cursorsCount))
                    {
                        //If we have a message that was successfully read by all consumers, remove this message from the queue and make it ready to purge.
                        if (messageRead is not null && cursorProcessedMessageCount.Increment() >= cursorsCount.Count)
                        {
                            _messagesToPurge.Add(messageRead);
                            lazyQueue.Value.TryDequeue(out _);
                            _activeStreamCursorsProcessedMessageCount.Remove(streamId, out var count);

                            if ((cursorsCount.Count - count.Count) != 0)
                            {
                            }
                        }
                        else if (messageRead is null)
                        {
                            //The messageRead object was null, so it means that the current message failed to process.
                            //But even failed messages (that are not purged) should be decreased from the count of current processing messages
                            //so that new messages can come in and restart the consumer cursor.
                            Interlocked.Decrement(ref _countProcessingMessages);

                            //if there are no more messages after the current one, remove the stream from the dictionary
                            if (!lazyQueue.Value.TryPeek(out _))
                            {
                                _processingMessages.TryRemove(streamId, out var oldQueue);
                                PreventNewMessagesRaceCondition(streamId, oldQueue);
                            }
                        }
                    }
                },
                //Since the message is being retried we have to consider it as a message being processed again
                () => Interlocked.Increment(ref _countProcessingMessages), () =>
                {
                    if (_processingMessages.TryGetValue(streamId, out lazyQueue))
                    {
                        return lazyQueue.Value;
                    }

                    return new ConcurrentQueue<RabbitMqBatchContainer>();
                }, () =>
                {
                    //Decrement this cursor from the count of active cursors
                    if (_activeStreamCursorsCount.TryGetValue(streamId, out var cursorsCount) &&
                        cursorsCount.Decrement() == 0)
                    {
                        //Prevent race condition when removing cursor count
                        if (_activeStreamCursorsCount.TryRemove(streamId, out cursorsCount) && cursorsCount.Count > 0)
                        {
                            _activeStreamCursorsCount.AddOrUpdate(streamId, (_) => cursorsCount,
                                (_, currentCount) => currentCount.Increment());
                        }
                    }
                });

            _activeStreamCursorsCount.AddOrUpdate(streamId, (_) => 1, (_, currentCount) => currentCount.Increment());

            return cursor;
        }

        return null;
    }

    private void PreventNewMessagesRaceCondition(StreamId streamId,
        Lazy<ConcurrentQueue<RabbitMqBatchContainer>> oldQueue)
    {
        //In case the stream received new messages while we were removing it from the dictionary we need to re-add it
        if (!oldQueue.Value.IsEmpty)
        {
            _processingMessages.AddOrUpdate(streamId, _ => oldQueue, (_, newQueue) =>
            {
                while (newQueue.Value.TryDequeue(out var batch))
                {
                    oldQueue.Value.Enqueue(batch);
                }

                return oldQueue;
            });
        }
    }

    public bool IsUnderPressure() => _countProcessingMessages >= GetMaxAddCount();
}