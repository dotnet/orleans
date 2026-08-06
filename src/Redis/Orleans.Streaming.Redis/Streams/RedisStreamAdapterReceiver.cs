using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Streaming.Redis;

internal sealed class RedisStreamAdapterReceiver : IQueueAdapterReceiver
{
    private readonly Serializer<RedisStreamBatchContainer> _serializer;
    private RedisStreamStorage? _queue;
    private Task? _task;

    public RedisStreamAdapterReceiver(
        Serializer<RedisStreamBatchContainer> serializer,
        RedisStreamStorage queue)
    {
        _serializer = serializer;
        _queue = queue;
    }

    public async Task Initialize(TimeSpan timeout)
    {
        if (_queue != null) // check in case we already shut it down.
        {
            await _queue.ConnectAsync();
        }
    }

    public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
    {
        try
        {
            var queue = _queue;
            if (queue is null)
            {
                return new List<IBatchContainer>();
            }

            var count = maxCount is < 0 or QueueAdapterConstants.UNLIMITED_GET_QUEUE_MSG
                ? RedisStreamStorage.MaxNumberOfMsgToGet
                : Math.Min(maxCount, RedisStreamStorage.MaxNumberOfMsgToGet);

            var task = queue.GetMessagesAsync(count);
            _task = task;
            var messages = await task;

            return messages
                .Select(message => (IBatchContainer)RedisStreamBatchContainer.FromStreamEntry(_serializer, message, queue.FieldName))
                .ToList();
        }
        finally
        {
            _task = null;
        }
    }

    public async Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
    {
        try
        {
            var queue = _queue;
            if (messages.Count == 0 || queue is null)
            {
                return;
            }

            var highestEntry = messages
                .Cast<RedisStreamBatchContainer>()
                .Select(x => x.Entry)
                .MaxBy(entry => RedisStreamBatchContainer.ParseEntryId(entry.Id));

            var task = queue.DeliveredMessagesAsync(highestEntry);
            _task = task;
            await task;
        }
        finally
        {
            _task = null;
        }
    }

    public async Task Shutdown(TimeSpan timeout)
    {
        try
        {
            if (_task is not null)
            {
                await _task;
            }
        }
        finally
        {
            if (_queue is not null)
            {
                await _queue.ShutdownAsync();
            }

            _queue = null;
        }
    }
}
