using System;
using System.Text;
using Orleans.Runtime;
using Azure.Messaging.EventHubs;
using Orleans.Streaming.EventHubs;
using Orleans.Streams;

namespace ServiceBus.Tests.TestStreamProviders.EventHub
{
    public class StreamPerPartitionDataAdapter : EventHubDataAdapter
    {
        public StreamPerPartitionDataAdapter(Orleans.Serialization.Serializer serializer) : base(serializer) {}

        public override StreamPosition GetStreamPosition(string partition, EventData queueMessage)
        {
            var streamId = StreamId.Create(new StreamIdentity(GetPartitionGuid(partition), null));
            StreamSequenceToken token =
            new EventHubSequenceTokenV2(queueMessage.Offset.ToString(), queueMessage.SequenceNumber, 0);

            return new StreamPosition(streamId, token);
        }

        public static Guid GetPartitionGuid(string partition)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(partition);
            Array.Resize(ref bytes, 10);
            return new Guid(partition.GetHashCode(), bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], bytes[7], bytes[8], bytes[9]);
        }
    }
}