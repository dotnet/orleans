using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.WindowsAzure.Storage.Queue;
using Newtonsoft.Json;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;
using RuntimeRequestContext = Orleans.Runtime.RequestContext;

namespace Orleans.Providers.Streams.AzureQueue
{
    [Serializable]
    internal class AzureQueueBatchContainer : IBatchContainer
    {
        [JsonProperty]
        protected EventSequenceToken sequenceToken;

        [JsonProperty]
        protected List<object> events;

        [JsonProperty]
        protected Dictionary<string, object> requestContext;

        [NonSerialized]
        // Need to store reference to the original AQ CloudQueueMessage to be able to delete it later on.
        // Don't need to serialize it, since we are never interested in sending it to stream consumers.
        internal CloudQueueMessage CloudQueueMessage;

        public Guid StreamGuid { get; protected set; }

        public String StreamNamespace { get; protected set; }

        internal Dictionary<string, object> RequestContext => this.requestContext;

        public StreamSequenceToken SequenceToken
        {
            get { return sequenceToken; }
        }

        [JsonConstructor]
        internal AzureQueueBatchContainer(
            Guid streamGuid, 
            String streamNamespace,
            List<object> events,
            Dictionary<string, object> requestContext, 
            EventSequenceToken sequenceToken)
            : this(streamGuid, streamNamespace, events, requestContext)
        {
            this.sequenceToken = sequenceToken;
        }

        protected AzureQueueBatchContainer(Guid streamGuid, String streamNamespace, List<object> events, Dictionary<string, object> requestContext)
        {
            if (events == null) throw new ArgumentNullException("events", "Message contains no events");
            
            StreamGuid = streamGuid;
            StreamNamespace = streamNamespace;
            this.events = events;
            this.requestContext = requestContext;
        }

        protected AzureQueueBatchContainer()
        {
        }

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            return events.OfType<T>().Select((e, i) => Tuple.Create<T, StreamSequenceToken>(e, sequenceToken.CreateSequenceTokenForEvent(i)));
        }

        public bool ShouldDeliver(IStreamIdentity stream, object filterData, StreamFilterPredicate shouldReceiveFunc)
        {
            foreach (object item in events)
            {
                if (shouldReceiveFunc(stream, filterData, item))
                    return true; // There is something in this batch that the consumer is intereted in, so we should send it.
            }
            return false; // Consumer is not interested in any of these events, so don't send.
        }

        internal void SetSequenceToken(EventSequenceToken token)
        {
            this.sequenceToken = token;
        }

        internal static CloudQueueMessage ToCloudQueueMessage<T>(Guid streamGuid, String streamNamespace, IEnumerable<T> events, Dictionary<string, object> requestContext)
        {
            var azureQueueBatchMessage = new AzureQueueBatchContainerV2(streamGuid, streamNamespace, events.Cast<object>().ToList(), requestContext);
            var rawBytes = SerializationManager.SerializeToByteArray(azureQueueBatchMessage);

            //new CloudQueueMessage(byte[]) not supported in netstandard, taking a detour to set it
            var cloudQueueMessage = new CloudQueueMessage(null as string);
            cloudQueueMessage.SetMessageContent(rawBytes);
            return cloudQueueMessage;
        }

        internal static AzureQueueBatchContainer FromCloudQueueMessage(CloudQueueMessage cloudMsg, long sequenceId)
        {
            var azureQueueBatch = SerializationManager.DeserializeFromByteArray<AzureQueueBatchContainer>(cloudMsg.AsBytes);
            azureQueueBatch.CloudQueueMessage = cloudMsg;
            azureQueueBatch.sequenceToken = new EventSequenceTokenV2(sequenceId);
            return azureQueueBatch;
        }

        public bool ImportRequestContext()
        {
            if (requestContext != null)
            {
                RuntimeRequestContext.Import(requestContext);
                return true;
            }
            return false;
        }

        public override string ToString()
        {
            return string.Format("[AzureQueueBatchContainer:Stream={0},#Items={1}]", StreamGuid, events.Count);
            //return string.Format("[AzureBatch:#Items={0},Items{1}]", events.Count, Utils.EnumerableToString(events.Select((e, i) => String.Format("{0}-{1}", e, sequenceToken.CreateSequenceTokenForEvent(i)))));
        }
    }
}
