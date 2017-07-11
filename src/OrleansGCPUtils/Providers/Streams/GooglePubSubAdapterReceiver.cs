using Google.Cloud.PubSub.V1;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Providers.Streams
{
    public class GooglePubSubAdapterReceiver : IQueueAdapterReceiver
    {
        private readonly SerializationManager _serializationManager;
        private GooglePubSubDataManager _pubSub;
        private long _lastReadMessage;
        private Task _outstandingTask;
        private readonly Logger _logger;
        private readonly IGooglePubSubDataAdapter _dataAdapter;
        private readonly List<PendingDelivery> _pending;

        public QueueId Id { get; }

        public static IQueueAdapterReceiver Create(SerializationManager serializationManager, QueueId queueId, string projectId, string topicId,
            string deploymentId, IGooglePubSubDataAdapter dataAdapter, TimeSpan? deadline = null)
        {
            if (queueId == null) throw new ArgumentNullException(nameof(queueId));
            if (dataAdapter == null) throw new ArgumentNullException(nameof(dataAdapter));
            if (serializationManager == null) throw new ArgumentNullException(nameof(serializationManager));

            var pubSub = new GooglePubSubDataManager(projectId, topicId, queueId.ToString(), deploymentId, deadline);
            return new GooglePubSubAdapterReceiver(serializationManager, queueId, pubSub, dataAdapter);
        }

        private GooglePubSubAdapterReceiver(SerializationManager serializationManager, QueueId queueId, GooglePubSubDataManager pubSub, IGooglePubSubDataAdapter dataAdapter)
        {
            if (queueId == null) throw new ArgumentNullException(nameof(queueId));
            Id = queueId;
            _serializationManager = serializationManager;
            if (pubSub == null) throw new ArgumentNullException(nameof(pubSub));
            _pubSub = pubSub;

            if (dataAdapter == null) throw new ArgumentNullException(nameof(dataAdapter));
            _dataAdapter = dataAdapter;

            _logger = LogManager.GetLogger(GetType().Name, LoggerType.Provider);
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

                int count = maxCount < 0 ? 10 : maxCount;

                var task = pubSubRef.GetMessages(count);
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
                _outstandingTask = Task.WhenAll(deliveredMessages.Select(m => pubSubRef.AcknowledgeMessages(new[] { m })));

                try
                {
                    await _outstandingTask;
                }
                catch (Exception exc)
                {
                    _logger.Warn((int)GoogleErrorCode.AcknowledgeMessage,
                        $"Exception upon AcknowledgeMessages on queue {Id}. Ignoring.", exc);
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
