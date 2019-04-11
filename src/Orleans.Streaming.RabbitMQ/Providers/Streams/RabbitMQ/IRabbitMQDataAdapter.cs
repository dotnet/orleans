using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Providers.RabbitMQ.Streams.RabbitMQ
{
    public interface IRabbitMQDataAdapter
    {
        /// <summary>
        /// Creates a <seealso cref="RabbitMQMessage"/> vehicle to send the bunny message vehicle to the queue.
        /// </summary>
        RabbitMQMessage ToRabbitMQMessage<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, Dictionary<string, object> requestContext);

        /// <summary>
        /// Creates a batch container of <seealso cref="RabbitMQMessage"/> vehicles.
        /// </summary>
        IBatchContainer FromPullResponseMessage(RabbitMQMessage msg, long sequenceId);
    }

    public class RabbitMQDataAdapter : IRabbitMQDataAdapter, IOnDeserialized
    {
        private SerializationManager _serializationManager;

        public object ByteString { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <seealso cref="IRabbitMQDataAdapter"/> interface.
        /// </summary>
        /// <param name="serializationManager">The <seealso cref="SerializationManager"/> injected at runtime.</param>
        public RabbitMQDataAdapter(SerializationManager serializationManager)
        {
            _serializationManager = serializationManager;
        }

        /// <inherithdoc/>
        public IBatchContainer FromPullResponseMessage(RabbitMQMessage msg, long sequenceId)
        {
            var batchContainer = _serializationManager.DeserializeFromByteArray<RabbitMQBatchContainer>(Serialise(msg));
            batchContainer.RealSequenceToken = new EventSequenceTokenV2(sequenceId);
            return batchContainer;
        }

        private byte[] Serialise(RabbitMQMessage msg)
        {
            if (msg == null) return null;
            var bf = new BinaryFormatter();
            using (var ms = new MemoryStream())
            {
                bf.Serialize(ms, msg);
                return ms.ToArray();
            }
        }

        private RabbitMQMessage Deserialize(byte[] obj)
        {
            RabbitMQMessage msg = null;
            var ms = new MemoryStream();
            try
            {
                var formatter = new BinaryFormatter();
                msg = (RabbitMQMessage)formatter.Deserialize(ms);
            }
            finally
            {
                ms.Close();
            }
            return msg;
        }

        public RabbitMQMessage ToRabbitMQMessage<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, Dictionary<string, object> requestContext)
        {
            var batchMessage = new RabbitMQBatchContainer(streamGuid, streamNamespace, events.Cast<object>().ToList(), requestContext);
            var rawBytes = _serializationManager.SerializeToByteArray(batchMessage);
            return Deserialize(rawBytes);
        }

        public void OnDeserialized(ISerializerContext context)
        {
            _serializationManager = context.GetSerializationManager();
        }
    }
}
