using Google.Cloud.PubSub.V1;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Providers.GCP.Streams.PubSub
{
    public class PubSubAdapterReceiver : IQueueAdapterReceiver
    {
        private PubSubDataManager _pubSub;
        private long _lastReadMessage;
        private Task _outstandingTask;
        private readonly ILogger _logger;
        private readonly IPubSubDataAdapter _dataAdapter;
        private readonly List<PendingDelivery> _pending;

        public QueueId Id { get; }

        public static IQueueAdapterReceiver Create(ILoggerFactory loggerFactory, QueueId queueId, string projectId, string topicId,
            string serviceId, IPubSubDataAdapter dataAdapter, TimeSpan? deadline = null, string customEndpoint = "")
        {
            if (queueId.IsDefault) throw new ArgumentNullException(nameof(queueId));
            if (dataAdapter == null) throw new ArgumentNullException(nameof(dataAdapter));

            var pubSub = new PubSubDataManager(loggerFactory, projectId, topicId, queueId.ToString(), serviceId, deadline, customEndpoint);
            return new PubSubAdapterReceiver(loggerFactory, queueId, topicId, pubSub, dataAdapter);
        }

        private PubSubAdapterReceiver(ILoggerFactory loggerFactory, QueueId queueId, string topicId, PubSubDataManager pubSub, IPubSubDataAdapter dataAdapter)
        {
            if (queueId.IsDefault) throw new ArgumentNullException(nameof(queueId));
            Id = queueId;
            if (pubSub == null) throw new ArgumentNullException(nameof(pubSub));
            _pubSub = pubSub;

            if (dataAdapter == null) throw new ArgumentNullException(nameof(dataAdapter));
            _dataAdapter = dataAdapter;

            _logger = loggerFactory.CreateLogger($"{this.GetType().FullName}.{topicId}.{queueId}");
            _pending = new List<PendingDelivery>();
        }

        public Task Initialize(TimeSpan timeout)
        {
            if (_pubSub != null) return _pubSub.Initialize();

            return Task.CompletedTask;
        }

        public async Task Shutdown(TimeSpan timeout)
        {
            try
            {
                // await the last pending operation, so after we shutdown and stop this receiver we don't get async operation completions from pending operations.
                if (_outstandingTask != null)
                    await _outstandingTask;
            }
            finally
            {
                // remember that we shut down so we never try to read from the queue again.
                _pubSub = null;
            }
        }

        public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
        {
            try
            {
                var pubSubRef = _pubSub; // store direct ref, in case we are somehow asked to shutdown while we are receiving.    
                if (pubSubRef == null) return new List<IBatchContainer>();

                var task = pubSubRef.GetMessages(maxCount);
                _outstandingTask = task;
                IEnumerable<ReceivedMessage> messages = await task;

                List<IBatchContainer> pubSubMessages = new List<IBatchContainer>();
                foreach (var message in messages)
                {
                    IBatchContainer container = _dataAdapter.FromPullResponseMessage(message.Message, _lastReadMessage++);
                    pubSubMessages.Add(container);
                    _pending.Add(new PendingDelivery(container.SequenceToken, message));
                }

                return pubSubMessages;
            }
            finally
            {
                _outstandingTask = null;
            }
        }

        public async Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        {
            try
            {
                var pubSubRef = _pubSub; // store direct ref, in case we are somehow asked to shutdown while we are receiving.
                if (messages.Count == 0 || pubSubRef == null) return;
                // get sequence tokens of delivered messages
                List<StreamSequenceToken> deliveredTokens = messages.Select(message => message.SequenceToken).ToList();
                // find oldest delivered message
                StreamSequenceToken oldest = deliveredTokens.Max();
                // finalize all pending messages at or befor the oldest
                List<PendingDelivery> finalizedDeliveries = _pending
                    .Where(pendingDelivery => !pendingDelivery.Token.Newer(oldest))
                    .ToList();
                if (finalizedDeliveries.Count == 0) return;
                // remove all finalized deliveries from pending, regardless of if it was delivered or not.
                _pending.RemoveRange(0, finalizedDeliveries.Count);
                // get the queue messages for all finalized deliveries that were delivered.
                List<ReceivedMessage> deliveredMessages = finalizedDeliveries
                    .Where(finalized => deliveredTokens.Contains(finalized.Token))
                    .Select(finalized => finalized.Message)
                    .ToList();
                if (deliveredMessages.Count == 0) return;
                // delete all delivered queue messages from the queue.  Anything finalized but not delivered will show back up later
                _outstandingTask = pubSubRef.AcknowledgeMessages(deliveredMessages);

                try
                {
                    await _outstandingTask;
                }
                catch (Exception exc)
                {
                    _logger.LogWarning(
                        (int)GoogleErrorCode.AcknowledgeMessage,
                        exc,
                        "Exception upon AcknowledgeMessages on queue {Id}. Ignoring.",
                        Id);
                }
            }
            finally
            {
                _outstandingTask = null;
            }
        }

        private class PendingDelivery
        {
            public PendingDelivery(StreamSequenceToken token, ReceivedMessage message)
            {
                Token = token;
                Message = message;
            }

            public ReceivedMessage Message { get; }

            public StreamSequenceToken Token { get; }
        }
    }
}
