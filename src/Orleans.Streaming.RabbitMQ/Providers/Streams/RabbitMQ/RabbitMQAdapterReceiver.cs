using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Providers.RabbitMQ.Streams.RabbitMQ
{
    public class RabbitMQAdapterReceiver : IQueueAdapterReceiver
    {
        private readonly SerializationManager _serializationManager;
        private RabbitMQOptions _rabbitFact;
        private RabbitMQManager _manager;
        private long _lastReadMessage;
        private readonly ILogger _logger;
        private readonly IRabbitMQDataAdapter _dataAdapter;

        public QueueId Id { get; }

        public static IQueueAdapterReceiver Create(SerializationManager serializationManager,
                                                   ILoggerFactory loggerFactory,
                                                   QueueId queueId,
                                                   IRabbitMQDataAdapter dataAdapter,
                                                   RabbitMQOptions options)
        {
            if (queueId == null) throw new ArgumentNullException(nameof(queueId));
            if (dataAdapter == null) throw new ArgumentNullException(nameof(dataAdapter));
            if (serializationManager == null) throw new ArgumentNullException(nameof(serializationManager));

            return new RabbitMQAdapterReceiver(options, loggerFactory, queueId, serializationManager, dataAdapter);
        }

        private RabbitMQAdapterReceiver(RabbitMQOptions options,
                                        ILoggerFactory loggerFactory,
                                        QueueId queueId,
                                        SerializationManager serializationManager,
                                        IRabbitMQDataAdapter rabbitMQAdapter)
        {
            _rabbitFact = options ?? throw new ArgumentNullException(nameof(options));
            _logger = loggerFactory.CreateLogger($"{this.GetType().FullName}.{queueId}");
            _serializationManager = serializationManager;
            _dataAdapter = rabbitMQAdapter;
        }

        public Task Initialize(TimeSpan timeout)
        {
            _manager = new RabbitMQManager(_rabbitFact, _logger);
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("instantiated rabbitmq colony");
            }
            return Task.CompletedTask;
        }

        public Task Shutdown(TimeSpan timeout)
        {

            _manager.Dispose();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Retrieves a message from the queue.
        /// </summary>
        /// <param name="maxCount">Number of messages to receive.</param>
        /// <returns></returns>
        public Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount = 1)
        {
            var colonyRef = _manager;
            if (colonyRef == null) return Task.FromResult<IList<IBatchContainer>>(null);

            var upstream = new List<IBatchContainer>();

            // rabbitmq does not have a pullN interface, so we just loop through how many messages we want to pull.
            for (var i = 0; i < maxCount; i++)
            {
                var msg = colonyRef.ReceiveMessage();
                var container = _dataAdapter.FromPullResponseMessage(msg, _lastReadMessage++);
                upstream.Add(container);
            }

            return Task.FromResult<IList<IBatchContainer>>(upstream);
        }

        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        {
            throw new NotImplementedException();
        }
    }
}
