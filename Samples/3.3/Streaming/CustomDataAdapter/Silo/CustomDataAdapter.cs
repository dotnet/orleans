using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Azure.EventHubs;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;

namespace Silo
{
    // Custom EventHubDataAdapter that serialize event using System.Text.Json
    public class CustomDataAdapter : EventHubDataAdapter
    {
        public CustomDataAdapter(SerializationManager serializationManager) : base(serializationManager)
        {
        }

        public override string GetPartitionKey(Guid streamGuid, string streamNamespace)
            => streamGuid.ToString();

        public override IStreamIdentity GetStreamIdentity(EventData queueMessage)
        {
            var guid = Guid.Parse(queueMessage.SystemProperties.PartitionKey);
            var ns = (string) queueMessage.Properties["StreamNamespace"];
            return new StreamIdentity(guid, ns);
        }

        public override EventData ToQueueMessage<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
            => throw new NotSupportedException("This adapter only supports read");

        protected override IBatchContainer GetBatchContainer(EventHubMessage eventHubMessage)
            => new CustomBatchContainer(eventHubMessage);
    }

    public class CustomBatchContainer : IBatchContainer
    {
        public Guid StreamGuid { get; }

        public string StreamNamespace { get; }

        public StreamSequenceToken SequenceToken { get; }

        private readonly byte[] payload;

        public CustomBatchContainer(EventHubMessage eventHubMessage)
        {
            StreamGuid = eventHubMessage.StreamIdentity.Guid;
            StreamNamespace = eventHubMessage.StreamIdentity.Namespace;
            SequenceToken = new EventHubSequenceTokenV2(eventHubMessage.Offset, eventHubMessage.SequenceNumber, 0);
            payload = eventHubMessage.Payload;
        }

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            try
            {
                var evt = JsonSerializer.Deserialize<T>(payload);
                return new[] { Tuple.Create(evt, SequenceToken) };
            }
            catch (Exception)
            {
                return new List<Tuple<T, StreamSequenceToken>>();
            }
        }

        public bool ImportRequestContext() => false;

        public bool ShouldDeliver(IStreamIdentity stream, object filterData, StreamFilterPredicate shouldReceiveFunc) => true;
    }
}
