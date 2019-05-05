using System;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.RabbitMQ.Providers
{
    /// <summary>
    /// Default RabbitMQ data adapter which users may subclass to override event data to stream mapping.
    /// </summary>
    public class RabbitMQDataAdapter : ICacheDataAdapter<RabbitMQMessage, CachedRabbitMQMessage>
    {
        private readonly SerializationManager _serializationManager;
        private readonly IObjectPool<FixedSizeBuffer> _bufferPool;
        private FixedSizeBuffer _currentBuffer;

        /// <inheritdoc />
        public Action<FixedSizeBuffer> OnBlockAllocated { set; private get; }

        public RabbitMQDataAdapter(SerializationManager serializationManager, IObjectPool<FixedSizeBuffer> bufferPool)
        {
            _serializationManager = serializationManager ?? throw new ArgumentNullException(nameof(serializationManager));
            _bufferPool = bufferPool ?? throw new ArgumentNullException(nameof(bufferPool));
        }

        public IBatchContainer GetBatchContainer(ref CachedRabbitMQMessage cachedMessage)
        {
            var rabbitMessage = new RabbitMQMessage(cachedMessage, _serializationManager);
            return GetBatchContainer(rabbitMessage);
        }

        protected virtual IBatchContainer GetBatchContainer(RabbitMQMessage rabbitMQMessage)
        {
            // undone (mxplusb): implement RabbitMQBatchContainer
            return new RabbitMQBatchContainer(rabbitMQMessage, _serializationManager);
        }

        /// <inheritdoc />
        public DateTime? GetMessageDequeueTimeUtc(ref CachedRabbitMQMessage message)
        {
            return message.DequeueTimeUtc;
        }

        /// <inheritdoc />
        public DateTime? GetMessageEnqueueTimeUtc(ref CachedRabbitMQMessage message)
        {
            return null;
        }

        public StreamSequenceToken GetSequenceToken(ref CachedRabbitMQMessage cachedMessage)
        {
            throw new NotImplementedException();
        }

        public StreamPosition GetStreamPosition(RabbitMQMessage queueMessage)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Converts a TQueueMessage message from the queue to a TCachedMessage cachable structures.
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <param name="queueMessage"></param>
        /// <param name="dequeueTimeUtc"></param>
        /// <returns></returns>
        public StreamPosition QueueMessageToCachedMessage(ref CachedRabbitMQMessage cachedMessage, RabbitMQMessage queueMessage, DateTime dequeueTimeUtc)
        {
            var streamPosition = GetStreamPosition(queueMessage);
            cachedMessage.StreamGuid = streamPosition.StreamIdentity.Guid;
            cachedMessage.SequenceNumber = (ulong)queueMessage.SequenceNumber;
            cachedMessage.EnqueueTimeUtc = queueMessage.EnqueueTimeUtc;
            cachedMessage.DequeueTimeUtc = dequeueTimeUtc;
            cachedMessage.Message = queueMessage.Message;
            return streamPosition;
        }
    }
}
