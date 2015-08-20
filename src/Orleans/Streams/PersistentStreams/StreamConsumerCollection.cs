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
        private readonly Dictionary<GuidId, StreamConsumerData> queueData; // map of consumers for one queue: from Guid ConsumerId to StreamConsumerData

        public StreamConsumerCollection()
        {
            queueData = new Dictionary<GuidId, StreamConsumerData>();
        }

        public StreamConsumerData AddConsumer(GuidId subscriptionId, StreamId streamId, IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter)
        {
            var consumerData = new StreamConsumerData(subscriptionId, streamId, streamConsumer, filter);
            queueData.Add(subscriptionId, consumerData);
            return consumerData;
        }

        public bool RemoveConsumer(GuidId subscriptionId)
        {
            StreamConsumerData consumer;
            if (!queueData.TryGetValue(subscriptionId, out consumer)) return false;

            if (consumer.Cursor != null)
            {
                // kill cursor activity and ensure it does not start again on this consumer data.
                consumer.Cursor.Dispose();
                consumer.Cursor = null; 
            }
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

        public IEnumerable<StreamConsumerData> AllConsumersForStream(StreamId streamId)
        {
            return queueData.Values.Where(consumer => consumer.StreamId.Equals(streamId));
        }

        public int Count
        {
            get { return queueData.Count; }
        }
    }
}
