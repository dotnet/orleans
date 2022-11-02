using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Streams
{
    [Serializable]
    [GenerateSerializer]
    internal sealed class StreamConsumerCollection
    {
        [Id(0)]
        private readonly Dictionary<GuidId, StreamConsumerData> queueData; // map of consumers for one stream: from Guid ConsumerId to StreamConsumerData
        [Id(1)]
        private DateTime lastActivityTime;

        [Id(2)]
        public bool StreamRegistered { get; set; }

        public StreamConsumerCollection(DateTime now)
        {
            queueData = new Dictionary<GuidId, StreamConsumerData>();
            lastActivityTime = now;
        }

        public StreamConsumerData AddConsumer(GuidId subscriptionId, QualifiedStreamId streamId, IStreamConsumerExtension streamConsumer, string filterData)
        {
            var consumerData = new StreamConsumerData(subscriptionId, streamId, streamConsumer, filterData);
            queueData.Add(subscriptionId, consumerData);
            lastActivityTime = DateTime.UtcNow;
            return consumerData;
        }

        public bool RemoveConsumer(GuidId subscriptionId, ILogger logger)
        {
            if (!queueData.Remove(subscriptionId, out var consumer)) return false;

            consumer.SafeDisposeCursor(logger);
            return true;
        }

        public bool Contains(GuidId subscriptionId)
        {
            return queueData.ContainsKey(subscriptionId);
        }

        public bool TryGetConsumer(GuidId subscriptionId, out StreamConsumerData data)
        {
            return queueData.TryGetValue(subscriptionId, out data);
        }

        public IEnumerable<StreamConsumerData> AllConsumers()
        {
            return queueData.Values;
        }

        public void DisposeAll(ILogger logger)
        {
            foreach (StreamConsumerData consumer in queueData.Values)
            {
                consumer.SafeDisposeCursor(logger);
            }
            queueData.Clear();
        }


        public int Count
        {
            get { return queueData.Count; }
        }

        public void RefreshActivity(DateTime now)
        {
            lastActivityTime = now;
        }

        public bool IsInactive(DateTime now, TimeSpan inactivityPeriod)
        {
            // Consider stream inactive (with all its consumers) from the pulling agent perspective if:
            // 1) There were no new events received for that stream in the last inactivityPeriod
            // 2) All consumer for that stream are currently inactive (that is, all cursors are inactive) - 
            //    meaning there is nothing for those consumers in the adapter cache.
            if (now - lastActivityTime < inactivityPeriod) return false;
            return !queueData.Values.Any(data => data.State.Equals(StreamConsumerDataState.Active));
        }
    }
}
