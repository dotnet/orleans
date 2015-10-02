/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Linq;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Streams
{
    [Serializable]
    internal class StreamConsumerCollection
    {
        private readonly Dictionary<GuidId, StreamConsumerData> queueData; // map of consumers for one stream: from Guid ConsumerId to StreamConsumerData
        private DateTime lastActivityTime;

        public StreamConsumerCollection(DateTime now)
        {
            queueData = new Dictionary<GuidId, StreamConsumerData>();
            lastActivityTime = now;
        }

        public StreamConsumerData AddConsumer(GuidId subscriptionId, StreamId streamId, IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter)
        {
            var consumerData = new StreamConsumerData(subscriptionId, streamId, streamConsumer, filter);
            queueData.Add(subscriptionId, consumerData);
            lastActivityTime = DateTime.UtcNow;
            return consumerData;
        }

        public bool RemoveConsumer(GuidId subscriptionId, Logger logger)
        {
            StreamConsumerData consumer;
            if (!queueData.TryGetValue(subscriptionId, out consumer)) return false;

            consumer.SafeDisposeCursor(logger);
            return queueData.Remove(subscriptionId);
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

        public void DisposeAll(Logger logger)
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
