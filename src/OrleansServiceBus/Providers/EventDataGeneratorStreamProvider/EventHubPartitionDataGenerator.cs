using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
#if NETSTANDARD
using Microsoft.Azure.EventHubs;
using static Microsoft.Azure.EventHubs.EventData;
#else
using Microsoft.ServiceBus.Messaging;
#endif
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Generate data for one stream
    /// </summary>
    internal class SimpleStreamEventDataGenerator : IStreamDataGenerator<EventData>
    {
        public IStreamIdentity StreamId { get; set; }
        public IntCounter SequenceNumberCounter { set; private get; }
        private Logger logger;
        public bool ShouldProduce { private get; set; }
        private SerializationManager serializationManager;
        public SimpleStreamEventDataGenerator(IStreamIdentity streamId, EventHubGeneratorStreamProviderSettings settings,
            Logger logger, SerializationManager serializationManager)
        {
            this.StreamId = streamId;
            this.logger = logger.GetSubLogger(this.GetType().Name);
            this.ShouldProduce = true;
            this.serializationManager = serializationManager;
        }

        public bool TryReadEvents(int maxCount, out IEnumerable<EventData> events)
        {
            if (!this.ShouldProduce)
            {
                events = null;
                return false;
            }
            int count = maxCount;
            List<EventData> eventDataList = new List<EventData>();
            while (count-- > 0)
            {
                this.SequenceNumberCounter.Increment();
                var eventData = EventHubBatchContainer.ToEventData<int>(this.serializationManager, this.StreamId.Guid, this.StreamId.Namespace,
                    this.GenerateEvent(this.SequenceNumberCounter.Value), RequestContext.Export(this.serializationManager));
#if NETSTANDARD
//set partition key
                eventData.SetPartitionKey(this.StreamId.Guid.ToString());
#endif
                //set offset
                DateTime now = DateTime.UtcNow;
                var offSet = this.StreamId.Guid.ToString() + now.ToString();
                eventData.SetOffset(offSet);
                //set sequence number
                eventData.SetSequenceNumber(this.SequenceNumberCounter.Value);
                //set enqueue time
                eventData.SetEnqueuedTimeUtc(now);
                eventDataList.Add(eventData);
                this.logger.Info($"Generate data of SequemceNumber {SequenceNumberCounter.Value} for stream {this.StreamId.Namespace}-{this.StreamId.Guid}");
            }

            events = eventDataList;
            return eventDataList.Count > 0;
        }

        IEnumerable<int> GenerateEvent(int sequenceNumber)
        {
            var events = new List<int>();
            events.Add(sequenceNumber);
            return events;
        }
    }

    /// <summary>
    /// EHPartitionDataGenerator generate data for a EH partition, which can include data from different streams
    /// </summary>
    internal class EventHubPartitionDataGenerator : IDataGenerator<EventData>, IStreamDataGeneratingController
    {
        //differnt stream in the same partition should use the same sequenceNumberCounter
        private IntCounter sequenceNumberCounter = new IntCounter();
        private Logger logger;
        private List<IStreamDataGenerator<EventData>> generators;
        private SerializationManager serializationManager;
        private EventHubGeneratorStreamProviderSettings settings;
        public EventHubPartitionDataGenerator(Logger logger, SerializationManager serializationManager, EventHubGeneratorStreamProviderSettings settings)
        {
            this.logger = logger.GetSubLogger(this.GetType().Name);
            this.generators = new List<IStreamDataGenerator<EventData>>();
            this.serializationManager = serializationManager;
            this.settings = settings;
        }
        public void AddDataGeneratorForStream(IStreamIdentity streamId)
        {
            var generator = (IStreamDataGenerator<EventData>)Activator.CreateInstance(settings.StreamDataGeneratorType,
                streamId, settings, this.logger, this.serializationManager);
            generator.SequenceNumberCounter = sequenceNumberCounter;
            this.logger.Info($"Data generator set up on stream {streamId.Namespace}-{streamId.Guid.ToString()}.");
            this.generators.Add(generator);
        }
        public void StopProducingOnStream(IStreamIdentity streamId)
        {
            this.generators.ForEach(generator => {
                if (generator.StreamId.Equals(streamId))
                {
                    generator.ShouldProduce = false;
                    this.logger.Info($"Stop producing data on stream {streamId.Namespace}-{streamId.Guid.ToString()}.");
                }
            });
        }
        public bool TryReadEvents(int maxCount, out IEnumerable<EventData> events)
        {
            if (this.generators.Count == 0)
            {
                events = new List<EventData>();
                return false;
            }
            var eventDataList = new List<EventData>();
            var iterator = this.generators.AsEnumerable().GetEnumerator();
            var batchCount = maxCount / this.generators.Count;
            batchCount = batchCount == 0 ? batchCount + 1 : batchCount;
            while (eventDataList.Count < maxCount)
            {
                //if reach to the end of the list, reset iterator to the head
                if (!iterator.MoveNext())
                {
                    iterator.Reset();
                    iterator.MoveNext();
                }
                IEnumerable<EventData> eventData;
                var remainingCount = maxCount - eventDataList.Count;
                var count = remainingCount > batchCount ? batchCount : remainingCount;
                if (iterator.Current.TryReadEvents(count, out eventData))
                {
                    foreach (var data in eventData)
                    {
                        eventDataList.Add(data);
                    }
                }
            }
            iterator.Dispose();
            events = eventDataList.AsEnumerable();
            return eventDataList.Count > 0;
        }
    }
}
