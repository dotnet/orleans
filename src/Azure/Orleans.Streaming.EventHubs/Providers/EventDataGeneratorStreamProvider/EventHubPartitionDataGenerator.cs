using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Azure.Messaging.EventHubs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.Streaming.EventHubs.Testing
{
    /// <summary>
    /// Generate data for one stream
    /// </summary>
    public partial class SimpleStreamEventDataGenerator : IStreamDataGenerator<EventData>
    {
        /// <inheritdoc />
        public StreamId StreamId { get; set; }

        /// <inheritdoc />
        public IIntCounter SequenceNumberCounter { set; private get; }
        /// <inheritdoc />
        public bool ShouldProduce { private get; set; }

        private readonly ILogger logger;
        private readonly DeepCopier deepCopier;
        private readonly Serializer serializer;

        public SimpleStreamEventDataGenerator(StreamId streamId, ILogger<SimpleStreamEventDataGenerator> logger, DeepCopier deepCopier, Serializer serializer)
        {
            this.StreamId = streamId;
            this.logger = logger;
            this.ShouldProduce = true;
            this.deepCopier = deepCopier;
            this.serializer = serializer;
        }

        /// <inheritdoc />
        public bool TryReadEvents(int maxCount, out IEnumerable<EventData> events)
        {
            if (!this.ShouldProduce)
            {
                events = null;
                return false;
            }
            int count = maxCount;
            List<EventData> eventDataList = new List<EventData>(maxCount);
            while (count-- > 0)
            {
                this.SequenceNumberCounter.Increment();

                // Create an EventData instance with an empty body. The body will be set later
                // from the batch container's context. Because there is a need to explicitly set
                // broker-owned properties such as the offset, sequence number, and partition key,
                // an instance is created using the model factory, which avoids the need to set
                // directly via the underlying AMQP message.
                var eventData = EventHubsModelFactory.EventData(
                        eventBody: BinaryData.Empty,
                        partitionKey: StreamId.GetKeyAsString(),
                        offsetString: DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture),
                        sequenceNumber: this.SequenceNumberCounter.Value);

                EventHubBatchContainer.UpdateEventData(
                    eventData,
                    this.serializer,
                    this.StreamId,
                    GenerateEvent(this.SequenceNumberCounter.Value),
                    RequestContextExtensions.Export(this.deepCopier));

                eventDataList.Add(eventData);
                LogInfoGenerateData(this.SequenceNumberCounter.Value, this.StreamId);
            }

            events = eventDataList;
            return eventDataList.Count > 0;
        }

        private static IEnumerable<int> GenerateEvent(int sequenceNumber)
        {
            return [sequenceNumber];
        }

        public static Func<StreamId, IStreamDataGenerator<EventData>> CreateFactory(IServiceProvider services)
        {
            return (streamId) => ActivatorUtilities.CreateInstance<SimpleStreamEventDataGenerator>(services, streamId);
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Generate data of SequenceNumber {SequenceNumber} for stream {StreamId}"
        )]
        private partial void LogInfoGenerateData(int sequenceNumber, StreamId streamId);
    }

    /// <summary>
    /// EHPartitionDataGenerator generate data for a EH partition, which can include data from different streams
    /// </summary>
    public partial class EventHubPartitionDataGenerator : IDataGenerator<EventData>, IStreamDataGeneratingController
    {
        //differnt stream in the same partition should use the same sequenceNumberCounter
        private readonly EventDataGeneratorStreamOptions options;
        private readonly IntCounter sequenceNumberCounter = new IntCounter();
        private readonly ILogger logger;
        private readonly Func<StreamId, IStreamDataGenerator<EventData>> generatorFactory;
        private readonly List<IStreamDataGenerator<EventData>> generators;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="options"></param>
        /// <param name="generatorFactory"></param>
        /// <param name="logger"></param>
        public EventHubPartitionDataGenerator(EventDataGeneratorStreamOptions options, Func<StreamId, IStreamDataGenerator<EventData>> generatorFactory, ILogger logger)
        {
            this.options = options;
            this.generatorFactory = generatorFactory;
            this.generators = new List<IStreamDataGenerator<EventData>>();
            this.logger = logger;
        }
        /// <inheritdoc />
        public void AddDataGeneratorForStream(StreamId streamId)
        {
            var generator =  this.generatorFactory(streamId);
            generator.SequenceNumberCounter = sequenceNumberCounter;
            LogInfoOnStreamSetup(streamId);
            this.generators.Add(generator);
        }
        /// <inheritdoc />
        public void StopProducingOnStream(StreamId streamId)
        {
            this.generators.ForEach(generator => {
                if (generator.StreamId.Equals(streamId))
                {
                    generator.ShouldProduce = false;
                    LogInfoOnStreamStop(streamId);
                }
            });
        }
        /// <inheritdoc />
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

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Data generator set up on stream {StreamId}."
        )]
        private partial void LogInfoOnStreamSetup(StreamId streamId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Stop producing data on stream {StreamId}."
        )]
        private partial void LogInfoOnStreamStop(StreamId streamId);
    }
}
