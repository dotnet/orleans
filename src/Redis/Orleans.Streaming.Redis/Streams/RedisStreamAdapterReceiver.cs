using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Streaming.Redis;

internal sealed class RedisStreamAdapterReceiver : IQueueAdapterReceiver
{
    private readonly ILogger _logger;
    private readonly Serializer<RedisStreamBatchContainer> _serializer;
    private RedisStreamStorage _queue;
    private long _seqNumber;
    private Task _task;

    public RedisStreamAdapterReceiver(
        ILoggerFactory loggerFactory,
        Serializer<RedisStreamBatchContainer> serializer,
        RedisStreamStorage queue)
    {
        _logger = loggerFactory.CreateLogger<RedisStreamAdapterReceiver>();
        _serializer = serializer;
        _queue = queue;
    }

    public async Task Initialize(TimeSpan timeout)
    {
        if (_queue != null) // check in case we already shut it down.
        {
            await _queue.ConnectAsync();
            await _queue.CreateGroupAsync();
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

            return messages.Select(message => (IBatchContainer)RedisStreamBatchContainer.FromStreamEntry(_serializer, message, _seqNumber++)).ToList();
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

            var task = queue.DeliveredMessagesAsync(messages.Cast<RedisStreamBatchContainer>().Select(x => x.Entry));
            _task = task;

            try
            {
                await task;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Acknowledge messages exception on queue {QueueId}.", queue.QueueId);
            }
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
            _queue = null;
        }
    }
}
